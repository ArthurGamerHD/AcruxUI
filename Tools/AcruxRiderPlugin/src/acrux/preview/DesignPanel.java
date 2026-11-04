package acrux.preview;

import java.awt.BorderLayout;
import java.awt.Color;
import java.awt.Dimension;
import java.awt.GridBagConstraints;
import java.awt.GridBagLayout;
import java.awt.Insets;
import java.util.List;
import javax.swing.BorderFactory;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JComboBox;
import javax.swing.JComponent;
import javax.swing.JLabel;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.JSpinner;
import javax.swing.JTextField;
import javax.swing.SpinnerNumberModel;
import javax.swing.Timer;

/**
 * The preview's design data panel: an editor for each design property of the view (the public static settable
 * properties of its design data class), as the host reports them. Edits are saved for the view and sent to the host,
 * which rebuilds the view with them. One panel per pane (the editor's and a detached window's).
 */
final class DesignPanel extends JPanel {
    private final PreviewSession session;
    private final JPanel rows = new JPanel(new GridBagLayout());
    private List<DesignProperty> shown = List.of();

    DesignPanel(PreviewSession session) {
        super(new BorderLayout());
        this.session = session;
        setBorder(BorderFactory.createMatteBorder(0, 1, 0, 0, new Color(0x3C, 0x3F, 0x41)));
        JPanel header = new JPanel(new BorderLayout());
        header.setBorder(BorderFactory.createEmptyBorder(4, 6, 4, 4));
        header.add(new JLabel("Design data"), BorderLayout.WEST);
        JButton reset = new JButton("Defaults");
        reset.setToolTipText("Back to the design data's own values");
        reset.addActionListener(e -> {
            session.designValues.clear();
            session.sendDesignValues();
            shown = List.of();
        });
        header.add(reset, BorderLayout.EAST);
        add(header, BorderLayout.NORTH);
        JPanel top = new JPanel(new BorderLayout());
        top.add(rows, BorderLayout.NORTH);
        JScrollPane scroll = new JScrollPane(top);
        scroll.setBorder(BorderFactory.createEmptyBorder());
        add(scroll, BorderLayout.CENTER);
        setPreferredSize(new Dimension(270, 100));
        setMinimumSize(new Dimension(160, 60));
        show(List.of());
    }

    /** The view's design properties; the editors are rebuilt when the properties (not just their values) change. */
    void show(List<DesignProperty> properties) {
        if (!shown.isEmpty() && DesignProperty.sameShape(shown, properties))
            return;
        shown = properties;
        rows.removeAll();
        if (properties.isEmpty()) {
            JLabel empty = new JLabel("<html>No design properties. Public static settable properties (string, bool, "
                    + "int, float, enum, Color) of the view's Design.DataContext class show here.</html>");
            empty.setForeground(new Color(0x80, 0x80, 0x80));
            rows.add(empty, constraints(0, 0, 2));
        }
        for (int i = 0; i < properties.size(); i++) {
            DesignProperty property = properties.get(i);
            JLabel label = new JLabel(property.label());
            label.setToolTipText(property.name + " (" + property.kind + ")");
            rows.add(label, constraints(0, i, 1));
            rows.add(editor(property), constraints(1, i, 1));
        }
        rows.revalidate();
        rows.repaint();
    }

    private static GridBagConstraints constraints(int x, int y, int width) {
        GridBagConstraints c = new GridBagConstraints();
        c.gridx = x;
        c.gridy = y;
        c.gridwidth = width;
        c.weightx = x == 0 && width == 1 ? 0 : 1;
        c.fill = GridBagConstraints.HORIZONTAL;
        c.anchor = GridBagConstraints.WEST;
        c.insets = new Insets(2, 6, 2, 6);
        return c;
    }

    private void set(DesignProperty property, String value) {
        session.designValues.set(property.name, value);
        session.sendDesignValues();
    }

    private JComponent editor(DesignProperty property) {
        switch (property.kind) {
            case "bool": {
                JCheckBox box = new JCheckBox();
                box.setSelected("true".equalsIgnoreCase(property.value));
                box.addActionListener(e -> set(property, box.isSelected() ? "true" : "false"));
                return box;
            }
            case "int": {
                JSpinner spinner = new JSpinner(new SpinnerNumberModel(parseLong(property.value), Long.MIN_VALUE, Long.MAX_VALUE, 1L));
                spinner.addChangeListener(e -> set(property, String.valueOf(((Number) spinner.getValue()).longValue())));
                return spinner;
            }
            case "float": {
                JSpinner spinner = new JSpinner(new SpinnerNumberModel(parseDouble(property.value), -1e9, 1e9, 0.05));
                spinner.setEditor(new JSpinner.NumberEditor(spinner, "0.0###"));
                spinner.addChangeListener(e -> set(property, Double.toString(((Number) spinner.getValue()).doubleValue())));
                return spinner;
            }
            case "enum": {
                JComboBox<String> box = new JComboBox<>(property.options.toArray(new String[0]));
                box.setSelectedItem(property.value);
                box.addActionListener(e -> {
                    Object value = box.getSelectedItem();
                    if (value != null)
                        set(property, value.toString());
                });
                return box;
            }
            case "color": {
                // RGB from the platform colour picker; the value's alpha is kept.
                String alpha = property.value.length() == 9 ? property.value.substring(1, 3) : "";
                com.intellij.ui.ColorPanel swatch = new com.intellij.ui.ColorPanel();
                swatch.setSelectedColor(parseColor(property.value));
                swatch.addActionListener(e -> {
                    Color chosen = swatch.getSelectedColor();
                    if (chosen != null)
                        set(property, "#" + alpha + PreviewSettings.hex(chosen).substring(1));
                });
                return swatch;
            }
            default: {
                // Text: sent a moment after typing stops, and on Enter.
                JTextField field = new JTextField(property.value);
                Timer debounce = new Timer(400, e -> set(property, field.getText()));
                debounce.setRepeats(false);
                field.getDocument().addDocumentListener(new javax.swing.event.DocumentListener() {
                    @Override
                    public void insertUpdate(javax.swing.event.DocumentEvent e) {
                        debounce.restart();
                    }

                    @Override
                    public void removeUpdate(javax.swing.event.DocumentEvent e) {
                        debounce.restart();
                    }

                    @Override
                    public void changedUpdate(javax.swing.event.DocumentEvent e) {
                        debounce.restart();
                    }
                });
                field.addActionListener(e -> {
                    debounce.stop();
                    set(property, field.getText());
                });
                return field;
            }
        }
    }

    private static long parseLong(String text) {
        try {
            return Long.parseLong(text.trim());
        } catch (RuntimeException error) {
            return 0L;
        }
    }

    private static double parseDouble(String text) {
        try {
            return Double.parseDouble(text.trim());
        } catch (RuntimeException error) {
            return 0d;
        }
    }

    private static Color parseColor(String text) {
        try {
            String hex = text.startsWith("#") ? text.substring(1) : text;
            if (hex.length() == 8)
                hex = hex.substring(2);
            return new Color(Integer.parseInt(hex, 16));
        } catch (RuntimeException error) {
            return Color.WHITE;
        }
    }
}
