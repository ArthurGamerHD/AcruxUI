package acrux.preview;

import com.intellij.openapi.Disposable;
import com.intellij.openapi.application.ApplicationManager;
import com.intellij.openapi.util.Computable;
import com.intellij.openapi.editor.Document;
import com.intellij.openapi.editor.event.DocumentEvent;
import com.intellij.openapi.editor.event.DocumentListener;
import com.intellij.openapi.fileEditor.FileDocumentManager;
import com.intellij.openapi.fileEditor.FileEditor;
import com.intellij.openapi.fileEditor.FileEditorState;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.util.UserDataHolderBase;
import com.intellij.openapi.vfs.VirtualFile;
import java.awt.BorderLayout;
import java.awt.Color;
import java.awt.FlowLayout;
import java.beans.PropertyChangeListener;
import java.io.File;
import javax.swing.BorderFactory;
import javax.swing.JComboBox;
import javax.swing.JComponent;
import javax.swing.JLabel;
import javax.swing.JPanel;
import javax.swing.JSpinner;
import javax.swing.SpinnerNumberModel;
import javax.swing.Timer;

/** The preview half of the split editor: a toolbar with the simulated LCD settings above the rendered frame. */
final class AcruxPreviewEditor extends UserDataHolderBase implements FileEditor {
    private final Project project;
    private final VirtualFile file;
    private final JPanel root = new JPanel(new BorderLayout());
    private final PreviewPanel preview = new PreviewPanel();
    private final PreviewSettings settings;
    private final PreviewSession session;
    private final Timer debounce;
    private JComponent toolbar;
    private final JPanel content = new JPanel(new BorderLayout());
    /** The languages the host reported (the game's); until then only the saved choice. */
    private java.util.List<String> languages = java.util.List.of();
    private javax.swing.JFrame detached;
    /** The design data panels (the editor's, and a detached window's) and the properties they show. */
    private final java.util.List<DesignPanel> designPanels = new java.util.ArrayList<>();
    private java.util.List<DesignProperty> designProperties = java.util.List.of();
    private DesignPanel designPanel;

    AcruxPreviewEditor(Project project, VirtualFile file) {
        this.project = project;
        this.file = file;
        this.settings = PreviewSettings.load(project);
        Document document = FileDocumentManager.getInstance().getDocument(file);
        session = new PreviewSession(new File(file.getPath()), () -> document == null ? "" : ApplicationManager.getApplication().runReadAction((Computable<String>) document::getText), preview, settings,
                DesignValues.load(project, file.getPath()));
        debounce = new Timer(250, event -> session.sendXaml());
        debounce.setRepeats(false);
        if (document != null)
            document.addDocumentListener(new DocumentListener() {
                @Override
                public void documentChanged(DocumentEvent event) {
                    debounce.restart();
                }
            }, this);
        session.languagesListener = this::showLanguages;
        session.designListener = this::showDesignProperties;
        toolbar = buildToolbar();
        content.add(toolbar, BorderLayout.NORTH);
        designPanel = newDesignPanel();
        designPanel.setVisible(settings.designPanel);
        content.add(split(preview, designPanel), BorderLayout.CENTER);
        root.add(content, BorderLayout.CENTER);
        installPointer(preview);
        root.addComponentListener(new java.awt.event.ComponentAdapter() {
            @Override
            public void componentResized(java.awt.event.ComponentEvent event) {
                toolbar.revalidate();
            }
        });
        session.start();
    }

    private JComponent buildToolbar() {
        // Wraps onto more rows when the preview is narrow (and grows to fit them, unlike FlowLayout).
        JPanel bar = new JPanel(new com.intellij.util.ui.WrapLayout(FlowLayout.LEFT, 6, 3));
        bar.setBorder(BorderFactory.createMatteBorder(0, 0, 1, 0, new Color(0x3C, 0x3F, 0x41)));

        JComboBox<String> size = new JComboBox<>(PreviewSettings.SIZES);
        size.setEditable(true);
        size.setSelectedItem(settings.size);
        size.setToolTipText("LCD surface size (width x height); type a custom size if needed");
        size.addActionListener(e -> {
            Object value = size.getSelectedItem();
            if (value != null) {
                settings.size = value.toString();
                changed();
            }
        });
        bar.add(new JLabel("Surface"));
        bar.add(size);

        JComboBox<String> font = new JComboBox<>(PreviewSettings.FONTS);
        font.setSelectedItem(settings.font);
        font.addActionListener(e -> {
            settings.font = String.valueOf(font.getSelectedItem());
            changed();
        });
        bar.add(new JLabel("Font"));
        bar.add(font);

        JSpinner scale = new JSpinner(new SpinnerNumberModel(settings.fontScale, 0.1, 4.0, 0.05));
        scale.setToolTipText("Font scale (the theme's fontScale resource)");
        scale.addChangeListener(e -> {
            settings.fontScale = ((Number) scale.getValue()).doubleValue();
            changed();
        });
        bar.add(new JLabel("Size"));
        bar.add(scale);

        bar.add(colorButton("Background", () -> settings.background, c -> settings.background = c));
        bar.add(colorButton("Foreground", () -> settings.foreground, c -> settings.foreground = c));
        bar.add(colorButton("Seed", () -> settings.seed, c -> settings.seed = c));

        bar.add(new JLabel("Language"));
        bar.add(languageBox());

        javax.swing.JToggleButton design = new javax.swing.JToggleButton("Design data", settings.designPanel);
        design.setToolTipText("Edit the view's design properties (its design data class's public static properties)");
        design.addActionListener(e -> {
            settings.designPanel = design.isSelected();
            settings.save(project);
            designPanels.forEach(panel -> panel.setVisible(settings.designPanel));
            root.revalidate();
            if (detached != null)
                detached.getContentPane().revalidate();
        });
        bar.add(design);

        javax.swing.JButton reset = new javax.swing.JButton("Reset");
        reset.setToolTipText("Back to the game's default LCD colours and the default surface");
        reset.addActionListener(e -> {
            settings.resetToDefaults();
            changed();
            rebuildToolbar();
        });
        bar.add(reset);

        javax.swing.JButton detach = new javax.swing.JButton(detached == null ? "Detach" : "Attach");
        detach.setToolTipText("Show the preview in a separate window (or return it to the editor)");
        detach.addActionListener(e -> {
            if (detached == null)
                detach();
            else
                attach();
        });
        bar.add(detach);
        return bar;
    }

    // IntelliJ's own colour swatch: opens the platform colour picker.
    private JComponent colorButton(String name, java.util.function.Supplier<Color> get, java.util.function.Consumer<Color> set) {
        JPanel field = new JPanel(new FlowLayout(FlowLayout.LEFT, 3, 0));
        field.setOpaque(false);
        com.intellij.ui.ColorPanel swatch = new com.intellij.ui.ColorPanel();
        swatch.setSelectedColor(get.get());
        swatch.setToolTipText(name + " colour of the simulated theme");
        swatch.addActionListener(e -> {
            Color chosen = swatch.getSelectedColor();
            if (chosen != null) {
                set.accept(chosen);
                changed();
            }
        });
        field.add(new JLabel(name));
        field.add(swatch);
        return field;
    }

    // Mouse over the preview reaches the view like an LCD pointer: hover, press/release (left, right, middle) and wheel.
    private void installPointer(PreviewPanel target) {
        java.awt.event.MouseAdapter mouse = new java.awt.event.MouseAdapter() {
            @Override
            public void mouseMoved(java.awt.event.MouseEvent e) {
                send(RemoteConnection.POINTER_MOVED, e, 0, 0);
            }

            @Override
            public void mouseDragged(java.awt.event.MouseEvent e) {
                send(RemoteConnection.POINTER_MOVED, e, 0, 0);
            }

            @Override
            public void mousePressed(java.awt.event.MouseEvent e) {
                target.requestFocusInWindow();
                send(RemoteConnection.POINTER_PRESSED, e, button(e), 0);
            }

            @Override
            public void mouseReleased(java.awt.event.MouseEvent e) {
                send(RemoteConnection.POINTER_RELEASED, e, button(e), 0);
            }

            @Override
            public void mouseExited(java.awt.event.MouseEvent e) {
                session.pointer(RemoteConnection.POINTER_MOVED, -1, -1, 0, 0);
            }

            @Override
            public void mouseWheelMoved(java.awt.event.MouseWheelEvent e) {
                send(RemoteConnection.SCROLL, e, 0, -e.getPreciseWheelRotation());
            }

            private void send(String id, java.awt.event.MouseEvent e, int button, double wheel) {
                double[] point = target.toFrame(e.getX(), e.getY());
                if (point == null)
                    session.pointer(RemoteConnection.POINTER_MOVED, -1, -1, 0, 0);
                else
                    session.pointer(id, point[0], point[1], button, wheel);
            }

            // Avalonia's MouseButton: 1 left, 2 right, 3 middle.
            private int button(java.awt.event.MouseEvent e) {
                if (javax.swing.SwingUtilities.isRightMouseButton(e))
                    return 2;
                if (javax.swing.SwingUtilities.isMiddleMouseButton(e))
                    return 3;
                return 1;
            }
        };
        target.addMouseListener(mouse);
        target.addMouseMotionListener(mouse);
        target.addMouseWheelListener(mouse);
    }

    private void rebuildToolbar() {
        content.remove(toolbar);
        toolbar = buildToolbar();
        content.add(toolbar, BorderLayout.NORTH);
        content.revalidate();
        content.repaint();
    }

    // One combo box per toolbar (the editor's and a detached window's): a Swing component has a single parent.
    private JComboBox<String> languageBox() {
        java.util.List<String> items = new java.util.ArrayList<>(languages);
        if (!items.contains(settings.language))
            items.add(settings.language);
        JComboBox<String> box = new JComboBox<>(items.toArray(new String[0]));
        box.setSelectedItem(settings.language);
        box.setToolTipText("Language of the previewed texts (the game's and the mod's localization)");
        box.addActionListener(e -> {
            Object value = box.getSelectedItem();
            if (value != null && !value.toString().equals(settings.language)) {
                settings.language = value.toString();
                changed();
            }
        });
        return box;
    }

    private static final String SPLIT_KEY = "AcruxPreview.DesignPanel";
    private static final float SPLIT_DEFAULT = 0.72f;

    // The preview beside its design data panel, resizable by dragging the divider; the width is remembered (IDE-wide).
    // A hidden design panel leaves the preview the whole width.
    private static JComponent split(JComponent frame, DesignPanel design) {
        com.intellij.ui.OnePixelSplitter splitter = new com.intellij.ui.OnePixelSplitter(false, SPLIT_KEY, SPLIT_DEFAULT);
        splitter.setFirstComponent(frame);
        splitter.setSecondComponent(design);
        splitter.setHonorComponentsMinimumSize(true);
        return splitter;
    }

    // The same for the detached window: a plain Swing split pane, since IntelliJ's splitter dividers need an IDE frame
    // (OnePixelDivider looks for the IDE glass pane). It shares the remembered width with the editor's splitter.
    private static JComponent windowSplit(JComponent frame, DesignPanel design) {
        javax.swing.JSplitPane pane = new javax.swing.JSplitPane(javax.swing.JSplitPane.HORIZONTAL_SPLIT, true, frame, design);
        pane.setResizeWeight(1.0);
        pane.setBorder(BorderFactory.createEmptyBorder());
        Runnable place = () -> {
            boolean shown = design.isVisible();
            pane.setDividerSize(shown ? 5 : 0);
            if (shown && pane.getWidth() > 0)
                pane.setDividerLocation(savedProportion());
            pane.revalidate();
        };
        design.addComponentListener(new java.awt.event.ComponentAdapter() {
            @Override
            public void componentShown(java.awt.event.ComponentEvent event) {
                place.run();
            }

            @Override
            public void componentHidden(java.awt.event.ComponentEvent event) {
                place.run();
            }
        });
        pane.addComponentListener(new java.awt.event.ComponentAdapter() {
            private boolean placed;

            @Override
            public void componentResized(java.awt.event.ComponentEvent event) {
                if (!placed && pane.getWidth() > 0) {
                    placed = true;
                    place.run();
                }
            }
        });
        pane.addPropertyChangeListener(javax.swing.JSplitPane.DIVIDER_LOCATION_PROPERTY, event -> {
            if (design.isVisible() && pane.getWidth() > 0 && pane.getDividerSize() > 0)
                com.intellij.ide.util.PropertiesComponent.getInstance().setValue(SPLIT_KEY,
                        String.valueOf(pane.getDividerLocation() / (float) pane.getWidth()));
        });
        return pane;
    }

    private static double savedProportion() {
        try {
            String value = com.intellij.ide.util.PropertiesComponent.getInstance().getValue(SPLIT_KEY);
            double proportion = value == null ? SPLIT_DEFAULT : Double.parseDouble(value);
            return proportion > 0.1 && proportion < 0.95 ? proportion : SPLIT_DEFAULT;
        } catch (RuntimeException error) {
            return SPLIT_DEFAULT;
        }
    }

    private DesignPanel newDesignPanel() {
        DesignPanel panel = new DesignPanel(session);
        panel.show(designProperties);
        designPanels.add(panel);
        return panel;
    }

    private void showDesignProperties(java.util.List<DesignProperty> reported) {
        designProperties = reported;
        designPanels.forEach(panel -> panel.show(reported));
    }

    private void showLanguages(java.util.List<String> reported) {
        if (reported.isEmpty() || reported.equals(languages))
            return;
        languages = java.util.List.copyOf(reported);
        rebuildToolbar();
        if (detached != null) {
            JComponent window = (JComponent) detached.getContentPane();
            window.remove(detachedToolbar);
            detachedToolbar = buildToolbar();
            window.add(detachedToolbar, BorderLayout.NORTH);
            window.revalidate();
            window.repaint();
        }
    }

    // Like Avalonia's previewer: the preview can live in its own window. The window gets its own pane and toolbar
    // (Swing components are not moved between the IDE frame and the window); the session feeds both.
    private PreviewPanel detachedPreview;
    private JComponent detachedToolbar;
    private DesignPanel detachedDesign;

    private void detach() {
        detachedPreview = new PreviewPanel();
        installPointer(detachedPreview);
        session.addPanel(detachedPreview);

        root.remove(content);
        JPanel placeholder = new JPanel(new java.awt.GridBagLayout());
        javax.swing.JButton back = new javax.swing.JButton("Return preview to editor");
        back.addActionListener(e -> attach());
        placeholder.add(back);
        root.add(placeholder, BorderLayout.CENTER);
        root.revalidate();
        root.repaint();

        detached = new javax.swing.JFrame("Acrux Preview: " + file.getName());
        detached.setDefaultCloseOperation(javax.swing.WindowConstants.DO_NOTHING_ON_CLOSE);
        detached.addWindowListener(new java.awt.event.WindowAdapter() {
            @Override
            public void windowClosing(java.awt.event.WindowEvent e) {
                attach();
            }
        });
        JPanel window = new JPanel(new BorderLayout());
        detachedToolbar = buildToolbar();
        window.add(detachedToolbar, BorderLayout.NORTH);
        detachedDesign = newDesignPanel();
        detachedDesign.setVisible(settings.designPanel);
        window.add(windowSplit(detachedPreview, detachedDesign), BorderLayout.CENTER);
        window.addComponentListener(new java.awt.event.ComponentAdapter() {
            @Override
            public void componentResized(java.awt.event.ComponentEvent event) {
                detachedToolbar.revalidate();
            }
        });
        detached.setContentPane(window);
        detached.setSize(Math.max(720, root.getWidth()), Math.max(600, root.getHeight()));
        detached.setLocationRelativeTo(root);
        detached.setVisible(true);
    }

    private void attach() {
        if (detached == null)
            return;
        javax.swing.JFrame frame = detached;
        detached = null;
        session.removePanel(detachedPreview);
        detachedPreview = null;
        detachedToolbar = null;
        designPanels.remove(detachedDesign);
        detachedDesign = null;
        frame.dispose();
        root.removeAll();
        root.add(content, BorderLayout.CENTER);
        rebuildToolbar();
        root.revalidate();
        root.repaint();
    }

    private void changed() {
        settings.save(project);
        session.sendSettings();
    }

    @Override
    public JComponent getComponent() {
        return root;
    }

    @Override
    public JComponent getPreferredFocusedComponent() {
        return preview;
    }

    @Override
    public String getName() {
        return "Acrux Preview";
    }

    @Override
    public void setState(FileEditorState state) {
    }

    @Override
    public boolean isModified() {
        return false;
    }

    @Override
    public boolean isValid() {
        return file.isValid();
    }

    @Override
    public void addPropertyChangeListener(PropertyChangeListener listener) {
    }

    @Override
    public void removePropertyChangeListener(PropertyChangeListener listener) {
    }

    @Override
    public VirtualFile getFile() {
        return file;
    }

    @Override
    public void dispose() {
        if (detached != null) {
            detached.dispose();
            detached = null;
        }
        debounce.stop();
        session.close();
    }
}
