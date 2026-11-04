package acrux.preview;

import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.HashMap;
import java.util.Map;
import java.util.UUID;
import java.util.function.BiConsumer;

/**
 * One connection of Avalonia's "tcp-bson" previewer transport: each message is a little-endian length, the message
 * type's GUID (in .NET's byte order) and a BSON document.
 */
final class RemoteConnection implements AutoCloseable {
    static final String START_DESIGNER_SESSION = "854887CF-2694-4EB6-B499-7461B6FB96C7";
    static final String UPDATE_XAML = "9AEC9A2E-6315-4066-B4BA-E9A9EFD0F8CC";
    static final String UPDATE_XAML_RESULT = "B7A70093-0C5D-47FD-9261-22086D43A2E2";
    static final String CLIENT_VIEWPORT_ALLOCATED = "BD7A8DE6-3DB8-4A13-8583-D6D4AB189A31";
    static final String REQUEST_VIEWPORT_RESIZE = "9B47B3D8-61DF-4C38-ACD4-8C1BB72554AC";
    static final String CLIENT_SUPPORTED_PIXEL_FORMATS = "63481025-7016-43FE-BADC-F2FD0F88609E";
    static final String FRAME_RECEIVED = "68014F8A-289D-4851-8D34-5367EDA7F827";
    static final String FRAME = "F58313EE-FE69-4536-819D-F52EDF201A0E";
    static final String POINTER_MOVED = "6228F0B9-99F2-4F62-A621-414DA2881648";
    static final String POINTER_PRESSED = "7E9E2818-F93F-411A-800E-6B1AEB11DA46";
    static final String POINTER_RELEASED = "4ADC84EE-E7C8-4BCF-986C-DE3A2F78EDE4";
    static final String SCROLL = "79301A05-F02D-4B90-BB39-472563B504AE";
    static final String CURSOR = "3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B102";
    static final String LANGUAGES = "3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B103";
    static final String DESIGN_PROPERTIES = "3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B104";
    static final String DESIGN_VALUES = "3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B105";
    static final String NOTICE = "3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B106";

    private static final Map<String, String> BY_BYTES = new HashMap<>();

    static {
        for (String id : new String[]{START_DESIGNER_SESSION, UPDATE_XAML, UPDATE_XAML_RESULT, CLIENT_VIEWPORT_ALLOCATED,
                REQUEST_VIEWPORT_RESIZE, CLIENT_SUPPORTED_PIXEL_FORMATS, FRAME_RECEIVED, FRAME, POINTER_MOVED,
                POINTER_PRESSED, POINTER_RELEASED, SCROLL, CURSOR, LANGUAGES, DESIGN_PROPERTIES, DESIGN_VALUES})
            BY_BYTES.put(key(dotNetBytes(id)), id);
    }

    private final Socket socket;
    private final InputStream input;
    private final OutputStream output;

    RemoteConnection(Socket socket) throws IOException {
        this.socket = socket;
        this.input = socket.getInputStream();
        this.output = socket.getOutputStream();
    }

    synchronized void send(String messageId, Map<String, Object> body) throws IOException {
        byte[] bson = Bson.write(body);
        ByteArrayOutputStream frame = new ByteArrayOutputStream(bson.length + 20);
        Bson.writeInt(frame, bson.length);
        frame.writeBytes(dotNetBytes(messageId));
        frame.writeBytes(bson);
        output.write(frame.toByteArray());
        output.flush();
    }

    /** Reads messages until the connection closes; unknown message types are skipped. */
    void readLoop(BiConsumer<String, Map<String, Object>> handler) throws IOException {
        byte[] header = new byte[20];
        while (true) {
            readExact(header);
            int length = ByteBuffer.wrap(header, 0, 4).order(ByteOrder.LITTLE_ENDIAN).getInt();
            byte[] guid = new byte[16];
            System.arraycopy(header, 4, guid, 0, 16);
            byte[] body = new byte[length];
            readExact(body);
            String id = BY_BYTES.get(key(guid));
            if (id != null)
                handler.accept(id, Bson.read(body));
        }
    }

    private void readExact(byte[] buffer) throws IOException {
        int read = 0;
        while (read < buffer.length) {
            int now = input.read(buffer, read, buffer.length - read);
            if (now < 0)
                throw new EOFException();
            read += now;
        }
    }

    // System.Guid.ToByteArray(): the first three groups are little-endian, the rest as written.
    static byte[] dotNetBytes(String guid) {
        UUID uuid = UUID.fromString(guid);
        ByteBuffer big = ByteBuffer.allocate(16);
        big.putLong(uuid.getMostSignificantBits());
        big.putLong(uuid.getLeastSignificantBits());
        byte[] b = big.array();
        return new byte[]{b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6],
                b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]};
    }

    private static String key(byte[] bytes) {
        StringBuilder text = new StringBuilder();
        for (byte b : bytes)
            text.append(String.format("%02x", b));
        return text.toString();
    }

    @Override
    public void close() throws IOException {
        socket.close();
    }
}
