package acrux.preview;

import java.awt.image.BufferedImage;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Properties;
import java.util.UUID;
import java.util.function.Supplier;
import javax.swing.SwingUtilities;

/**
 * Runs the toolkit's preview host for one Acrux view: listens for it, starts it, sends the edited XAML and shows the
 * frames it renders. The host is found through obj/acrux-preview.properties, written when the mod project builds.
 */
final class PreviewSession implements AutoCloseable {
    static final String PROPERTIES = "obj/acrux-preview.properties";

    private final File viewFile;
    private final Supplier<String> text;
    // Every pane showing this preview (the editor's, and a detached window's).
    private final java.util.List<PreviewPanel> panels = new java.util.concurrent.CopyOnWriteArrayList<>();
    private volatile BufferedImage lastFrame;
    private volatile String lastStatus = "Starting Acrux preview...";
    private volatile boolean lastStatusError;
    private volatile int lastCursor;
    private volatile String lastNotice;
    private final PreviewSettings settings;
    private volatile RemoteConnection connection;
    private volatile Process process;
    private volatile ServerSocket server;
    private volatile boolean closed;
    private volatile String assemblyPath;
    private volatile String projectRelativePath;
    private final StringBuilder hostOutput = new StringBuilder();
    /** The view's design property values the editors hold (sent to the host; saved per view by the editor). */
    final DesignValues designValues;
    /** Called (on the UI thread) with the view's design properties, after each build of the view. */
    volatile java.util.function.Consumer<List<DesignProperty>> designListener;
    /** Called (on the UI thread) with the languages the game ships, when the host reports them. */
    volatile java.util.function.Consumer<List<String>> languagesListener;

    PreviewSession(File viewFile, Supplier<String> text, PreviewPanel panel, PreviewSettings settings, DesignValues designValues) {
        this.designValues = designValues;
        this.viewFile = viewFile;
        this.text = text;
        this.panels.add(panel);
        this.settings = settings;
    }

    /** Shows this preview in another pane too (starting from the latest frame). Call on the UI thread. */
    void addPanel(PreviewPanel other) {
        panels.add(other);
        if (lastFrame != null)
            other.showFrame(lastFrame);
        if (lastStatus != null)
            other.showStatus(lastStatus, lastStatusError);
        other.showCursor(lastCursor);
        other.showNotice(lastNotice);
    }

    void removePanel(PreviewPanel other) {
        panels.remove(other);
    }

    /** Sends the simulated surface and theme; the host re-renders with them. */
    void sendSettings() {
        RemoteConnection current = connection;
        if (current == null)
            return;
        try {
            current.send(PreviewSettings.MESSAGE_ID, settings.toMessage());
        } catch (IOException error) {
            status("Acrux preview connection failed: " + error, true);
        }
    }

    /** Sends the editors' design property values; the host rebuilds the view with them. */
    void sendDesignValues() {
        RemoteConnection current = connection;
        if (current == null)
            return;
        Map<String, Object> message = new LinkedHashMap<>();
        List<Object> names = new ArrayList<>();
        List<Object> values = new ArrayList<>();
        for (Map.Entry<String, String> entry : designValues.values().entrySet()) {
            names.add(entry.getKey());
            values.add(entry.getValue());
        }
        message.put("Names", names);
        message.put("Values", values);
        try {
            current.send(RemoteConnection.DESIGN_VALUES, message);
        } catch (IOException error) {
            status("Acrux preview connection failed: " + error, true);
        }
    }

    void start() {
        Thread thread = new Thread(this::run, "Acrux preview: " + viewFile.getName());
        thread.setDaemon(true);
        thread.start();
        watcher.start();
    }

    // Post-build reload: a new build of the mod or of the preview host restarts the host. (Loading a rebuilt mod into
    // the running host clashes with what the old copy registered there: style owners, Acrux type identity.) Polled, so builds from Rider and the command line both count;
    // a change is acted on once the file has stopped changing (the build has finished writing it).
    private volatile String hostPath;
    private long assemblyStamp = -1, hostStamp = -1, pendingAssembly = -1, pendingHost = -1;
    private final javax.swing.Timer watcher = new javax.swing.Timer(1000, event -> checkBuilds());

    private boolean waitingForFirstBuild;

    private void checkBuilds() {
        if (closed)
            return;
        // Started before the project was ever built (no host found yet): start once a build writes its properties.
        if (connection == null && (assemblyPath == null || assemblyPath.isEmpty())) {
            File projectDir = findProjectDir(viewFile);
            boolean built = projectDir != null && new File(projectDir, PROPERTIES).isFile();
            if (built && waitingForFirstBuild) {
                waitingForFirstBuild = false;
                restart("Project built: starting the preview...");
            } else if (!built)
                waitingForFirstBuild = true;
            return;
        }
        // Only once the run has found both files: stamps taken earlier would read as a rebuild.
        if (hostPath == null || assemblyPath == null || assemblyPath.isEmpty())
            return;
        long assembly = stamp(assemblyPath), host = stamp(hostPath);
        if (assemblyStamp < 0 || hostStamp < 0) {
            assemblyStamp = assembly;
            hostStamp = host;
            return;
        }
        if (host == hostStamp && assembly == assemblyStamp) {
            pendingHost = pendingAssembly = -1;
            return;
        }
        // Wait for both files to stop changing (one more tick with the same stamps): the build has finished writing.
        if (host != pendingHost || assembly != pendingAssembly) {
            pendingHost = host;
            pendingAssembly = assembly;
            return;
        }
        if (host <= 0 || assembly <= 0)
            return;
        boolean hostRebuilt = host != hostStamp;
        hostStamp = host;
        assemblyStamp = assembly;
        pendingHost = pendingAssembly = -1;
        restart(hostRebuilt ? "Preview host rebuilt: restarting..." : "Mod rebuilt: reloading the preview...");
    }

    private static long stamp(String path) {
        if (path == null || path.isEmpty())
            return 0;
        File file = new File(path);
        return file.isFile() ? file.lastModified() : 0;
    }

    // The host process again (a rebuild, or a first build): the panes keep the last frame meanwhile.
    private void restart(String reason) {
        status(reason, false);
        runs.incrementAndGet(); // the old run's end is no longer reported
        Thread old = new Thread(() -> {
            closeQuietly();
            connection = null;
            server = null;
            process = null;
            Thread thread = new Thread(this::run, "Acrux preview: " + viewFile.getName());
            thread.setDaemon(true);
            thread.start();
        }, "Acrux preview restart");
        old.setDaemon(true);
        old.start();
    }

    // Each start of the host is a run; a restart's old run must not touch the new one's connection.
    private final java.util.concurrent.atomic.AtomicInteger runs = new java.util.concurrent.atomic.AtomicInteger();

    private void run() {
        int run = runs.incrementAndGet();
        ServerSocket ownServer = null;
        Process ownProcess = null;
        RemoteConnection ownConnection = null;
        try {
            File projectDir = findProjectDir(viewFile);
            if (projectDir == null) {
                status("This file is not inside a project (no .csproj found above it).", true);
                return;
            }
            File config = new File(projectDir, PROPERTIES);
            if (!config.isFile()) {
                status("Build " + projectDir.getName() + " once (Debug) so the Acrux preview can find its host.\n(" + config + " is missing.)", true);
                return;
            }
            Properties properties = new Properties();
            try (InputStream in = new FileInputStream(config)) {
                properties.load(in);
            }
            String host = properties.getProperty("host", "");
            String dotnet = properties.getProperty("dotnet", "");
            assemblyPath = properties.getProperty("assembly", "");
            projectRelativePath = "/" + projectDir.toPath().relativize(viewFile.toPath()).toString().replace('\\', '/');
            if (!new File(host).isFile()) {
                status("The Acrux preview host is not built:\n" + host + "\nBuild Acrux UI Tools/AcruxPreviewHost (Debug).", true);
                return;
            }
            if (dotnet.isEmpty() || !new File(dotnet).isFile())
                dotnet = "dotnet";
            hostPath = host;

            server = ownServer = new ServerSocket(0, 1, InetAddress.getLoopbackAddress());
            ownServer.setSoTimeout(30000);
            List<String> command = new ArrayList<>();
            command.add(dotnet);
            command.add(host);
            command.add("--transport");
            command.add("tcp-bson://127.0.0.1:" + ownServer.getLocalPort() + "/");
            command.add("--session-id");
            command.add(UUID.randomUUID().toString());
            command.add("--method");
            command.add("avalonia-remote");
            command.add(assemblyPath);
            ProcessBuilder builder = new ProcessBuilder(command).redirectErrorStream(true);
            process = ownProcess = builder.start();
            collectOutput(ownProcess);
            Socket socket = ownServer.accept();
            connection = ownConnection = new RemoteConnection(socket);
            status("Waiting for the first frame...", false);
            ownConnection.readLoop(this::onMessage);
        } catch (Exception error) {
            if (!closed && run == runs.get())
                status("Acrux preview stopped: " + error + hostTail(), true);
        } finally {
            closeQuietly(ownConnection, ownServer, ownProcess);
        }
    }

    private void onMessage(String id, Map<String, Object> body) {
        try {
            switch (id) {
                case RemoteConnection.START_DESIGNER_SESSION:
                    Map<String, Object> formats = new LinkedHashMap<>();
                    formats.put("Formats", List.of(2)); // Bgra8888
                    connection.send(RemoteConnection.CLIENT_SUPPORTED_PIXEL_FORMATS, formats);
                    sendSettings();
                    sendDesignValues();
                    sendXaml();
                    break;
                case RemoteConnection.REQUEST_VIEWPORT_RESIZE: {
                    Map<String, Object> viewport = new LinkedHashMap<>();
                    viewport.put("Width", number(body.get("Width")));
                    viewport.put("Height", number(body.get("Height")));
                    viewport.put("DpiX", 96.0);
                    viewport.put("DpiY", 96.0);
                    connection.send(RemoteConnection.CLIENT_VIEWPORT_ALLOCATED, viewport);
                    break;
                }
                case RemoteConnection.UPDATE_XAML_RESULT: {
                    Object error = body.get("Error");
                    if (error != null)
                        status(String.valueOf(error), true);
                    break;
                }
                case RemoteConnection.LANGUAGES: {
                    List<String> languages = new ArrayList<>();
                    Object list = body.get("Languages");
                    if (list instanceof List)
                        for (Object item : (List<?>) list)
                            if (item != null)
                                languages.add(item.toString());
                    java.util.function.Consumer<List<String>> listener = languagesListener;
                    if (listener != null)
                        SwingUtilities.invokeLater(() -> listener.accept(languages));
                    break;
                }
                case RemoteConnection.DESIGN_PROPERTIES: {
                    List<DesignProperty> properties = DesignProperty.fromMessage(body);
                    java.util.function.Consumer<List<DesignProperty>> listener = designListener;
                    if (listener != null)
                        SwingUtilities.invokeLater(() -> listener.accept(properties));
                    break;
                }
                case RemoteConnection.NOTICE: {
                    Object value = body.get("Text");
                    String notice = value == null ? null : value.toString();
                    lastNotice = notice;
                    SwingUtilities.invokeLater(() -> panels.forEach(p -> p.showNotice(notice)));
                    break;
                }
                case RemoteConnection.CURSOR: {
                    int cursor = (int) number(body.get("Cursor"));
                    lastCursor = cursor;
                    SwingUtilities.invokeLater(() -> panels.forEach(p -> p.showCursor(cursor)));
                    break;
                }
                case RemoteConnection.FRAME: {
                    BufferedImage image = toImage(body);
                    if (image != null)
                    {
                        lastFrame = image;
                        lastStatus = null;
                        SwingUtilities.invokeLater(() -> panels.forEach(p -> p.showFrame(image)));
                    }
                    Map<String, Object> received = new LinkedHashMap<>();
                    received.put("SequenceId", body.get("SequenceId") instanceof Number ? ((Number) body.get("SequenceId")).longValue() : 0L);
                    connection.send(RemoteConnection.FRAME_RECEIVED, received);
                    break;
                }
                default:
                    break;
            }
        } catch (IOException error) {
            status("Acrux preview connection failed: " + error, true);
        }
    }

    /** Mouse over the preview, in frame pixels (the frames are rendered at 96 DPI, so also surface pixels). */
    void pointer(String messageId, double x, double y, int button, double wheel) {
        RemoteConnection current = connection;
        if (current == null)
            return;
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("Modifiers", List.of());
        event.put("X", x);
        event.put("Y", y);
        if (messageId.equals(RemoteConnection.POINTER_PRESSED) || messageId.equals(RemoteConnection.POINTER_RELEASED))
            event.put("Button", button);
        if (messageId.equals(RemoteConnection.SCROLL)) {
            event.put("DeltaX", 0.0);
            event.put("DeltaY", wheel);
        }
        try {
            current.send(messageId, event);
        } catch (IOException error) {
            status("Acrux preview connection failed: " + error, true);
        }
    }

    /** Sends the editor's current text; called when the session starts and after edits. */
    void sendXaml() {
        RemoteConnection current = connection;
        if (current == null)
            return;
        Map<String, Object> update = new LinkedHashMap<>();
        update.put("Xaml", text.get());
        update.put("AssemblyPath", assemblyPath);
        update.put("XamlFileProjectPath", projectRelativePath);
        try {
            current.send(RemoteConnection.UPDATE_XAML, update);
        } catch (IOException error) {
            status("Acrux preview connection failed: " + error, true);
        }
    }

    private static BufferedImage toImage(Map<String, Object> frame) {
        Object data = frame.get("Data");
        int width = (int) number(frame.get("Width"));
        int height = (int) number(frame.get("Height"));
        int stride = (int) number(frame.get("Stride"));
        if (!(data instanceof byte[]) || width <= 0 || height <= 0)
            return null;
        byte[] pixels = (byte[]) data;
        int format = (int) number(frame.get("Format")); // 1 Rgba8888, 2 Bgra8888
        BufferedImage image = new BufferedImage(width, height, BufferedImage.TYPE_INT_ARGB);
        int[] row = new int[width];
        for (int y = 0; y < height; y++) {
            int offset = y * stride;
            for (int x = 0; x < width; x++) {
                int i = offset + x * 4;
                if (i + 3 >= pixels.length)
                    break;
                int c0 = pixels[i] & 0xFF, c1 = pixels[i + 1] & 0xFF, c2 = pixels[i + 2] & 0xFF, a = pixels[i + 3] & 0xFF;
                int r = format == 1 ? c0 : c2, b = format == 1 ? c2 : c0;
                row[x] = (a << 24) | (r << 16) | (c1 << 8) | b;
            }
            image.setRGB(0, y, width, 1, row, 0, width);
        }
        return image;
    }

    private static double number(Object value) {
        return value instanceof Number ? ((Number) value).doubleValue() : 0;
    }

    private void status(String text, boolean error) {
        lastStatus = text;
        lastStatusError = error;
        SwingUtilities.invokeLater(() -> panels.forEach(p -> p.showStatus(text, error)));
    }

    private void collectOutput(Process started) {
        Thread reader = new Thread(() -> {
            byte[] buffer = new byte[4096];
            try (InputStream in = started.getInputStream()) {
                int read;
                while ((read = in.read(buffer)) > 0)
                    synchronized (hostOutput) {
                        hostOutput.append(new String(buffer, 0, read, StandardCharsets.UTF_8));
                        if (hostOutput.length() > 8000)
                            hostOutput.delete(0, hostOutput.length() - 8000);
                    }
            } catch (IOException ignored) {
            }
        }, "Acrux preview host output");
        reader.setDaemon(true);
        reader.start();
    }

    private String hostTail() {
        synchronized (hostOutput) {
            if (hostOutput.length() == 0)
                return "";
            String text = hostOutput.toString().trim();
            int start = Math.max(0, text.length() - 600);
            return "\n" + text.substring(start);
        }
    }

    static File findProjectDir(File file) {
        for (File dir = file.getParentFile(); dir != null; dir = dir.getParentFile()) {
            File[] projects = dir.listFiles((d, name) -> name.endsWith(".csproj"));
            if (projects != null && projects.length > 0)
                return dir;
        }
        return null;
    }

    private void closeQuietly() {
        closeQuietly(connection, server, process);
    }

    private static void closeQuietly(RemoteConnection connection, ServerSocket server, Process process) {
        try {
            if (connection != null)
                connection.close();
        } catch (IOException ignored) {
        }
        try {
            if (server != null)
                server.close();
        } catch (IOException ignored) {
        }
        if (process != null)
            process.destroy();
    }

    @Override
    public void close() {
        closed = true;
        watcher.stop();
        closeQuietly();
    }
}
