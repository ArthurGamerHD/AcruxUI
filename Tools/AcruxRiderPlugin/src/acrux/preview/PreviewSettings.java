package acrux.preview;

import com.intellij.ide.util.PropertiesComponent;
import com.intellij.openapi.project.Project;
import java.awt.Color;
import java.util.LinkedHashMap;
import java.util.Map;

/** What the preview simulates: the LCD surface size and the design-time theme. Remembered per project. */
final class PreviewSettings {
    static final String MESSAGE_ID = "3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B101";

    /** Surface sizes of the game's LCDs (texture pixels). */
    static final String[] SIZES = {
            "512x512  LCD panel",
            "1024x512  Wide LCD panel",
            "512x307  Corner LCD",
            "512x85  Small corner LCD",
            "256x256  Small screen",
            "1024x1024  Large square",
    };
    static final String[] FONTS = {"(style default)", "White", "Monospace", "Debug"};

    String size = SIZES[0];
    String font = FONTS[0];
    double fontScale = 0.7;
    // The game's default LCD colours, and Arthur's LCD Mod's default faction colour as the theme seed.
    static final Color DEFAULT_BACKGROUND = new Color(0, 88, 151);
    static final Color DEFAULT_FOREGROUND = new Color(197, 237, 255);
    static final Color DEFAULT_SEED = new Color(58, 32, 63);

    Color background = DEFAULT_BACKGROUND;
    Color foreground = DEFAULT_FOREGROUND;
    Color seed = DEFAULT_SEED;
    String language = "en";
    /** The design data panel is shown. */
    boolean designPanel;

    void resetToDefaults() {
        size = SIZES[0];
        font = FONTS[0];
        fontScale = 0.7;
        background = DEFAULT_BACKGROUND;
        foreground = DEFAULT_FOREGROUND;
        seed = DEFAULT_SEED;
        language = "en";
    }

    int width() {
        return dimension(0, 512);
    }

    int height() {
        return dimension(1, 512);
    }

    private int dimension(int index, int fallback) {
        try {
            String[] parts = size.trim().split("\\s+")[0].split("x");
            return Math.max(16, Math.min(4096, Integer.parseInt(parts[index].trim())));
        } catch (RuntimeException error) {
            return fallback;
        }
    }

    Map<String, Object> toMessage() {
        Map<String, Object> message = new LinkedHashMap<>();
        message.put("Width", (double) width());
        message.put("Height", (double) height());
        message.put("FontId", font.startsWith("(") ? null : font);
        message.put("FontScale", fontScale);
        message.put("Background", hex(background));
        message.put("Foreground", hex(foreground));
        message.put("Seed", hex(seed));
        message.put("Language", language);
        return message;
    }

    static String hex(Color color) {
        return String.format("#%02X%02X%02X", color.getRed(), color.getGreen(), color.getBlue());
    }

    private static final String PREFIX = "acrux.preview.";

    static PreviewSettings load(Project project) {
        PreviewSettings settings = new PreviewSettings();
        PropertiesComponent store = PropertiesComponent.getInstance(project);
        settings.size = store.getValue(PREFIX + "size", settings.size);
        settings.font = store.getValue(PREFIX + "font", settings.font);
        try {
            settings.fontScale = Double.parseDouble(store.getValue(PREFIX + "fontScale", Double.toString(settings.fontScale)));
        } catch (NumberFormatException ignored) {
        }
        settings.background = color(store.getValue(PREFIX + "background"), settings.background);
        settings.foreground = color(store.getValue(PREFIX + "foreground"), settings.foreground);
        settings.seed = color(store.getValue(PREFIX + "seed"), settings.seed);
        settings.language = store.getValue(PREFIX + "language", settings.language);
        settings.designPanel = store.getBoolean(PREFIX + "designPanel", false);
        return settings;
    }

    void save(Project project) {
        PropertiesComponent store = PropertiesComponent.getInstance(project);
        store.setValue(PREFIX + "size", size);
        store.setValue(PREFIX + "font", font);
        store.setValue(PREFIX + "fontScale", Double.toString(fontScale));
        store.setValue(PREFIX + "background", hex(background));
        store.setValue(PREFIX + "foreground", hex(foreground));
        store.setValue(PREFIX + "seed", hex(seed));
        store.setValue(PREFIX + "language", language);
        store.setValue(PREFIX + "designPanel", designPanel, false);
    }

    private static Color color(String value, Color fallback) {
        try {
            return value == null ? fallback : Color.decode(value);
        } catch (NumberFormatException error) {
            return fallback;
        }
    }
}
