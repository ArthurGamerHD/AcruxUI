package acrux.preview;

import com.intellij.ide.util.PropertiesComponent;
import com.intellij.openapi.project.Project;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The design property values set in the preview's design data panel for one view file (by "Type.Property", as text).
 * Remembered per project and view, and sent to the host whenever the preview (re)connects.
 */
final class DesignValues {
    private static final String PREFIX = "acrux.preview.design:";
    private final Project project;
    private final String key;
    private final Map<String, String> values = new LinkedHashMap<>();

    private DesignValues(Project project, String key) {
        this.project = project;
        this.key = key;
    }

    static DesignValues load(Project project, String viewPath) {
        DesignValues design = new DesignValues(project, PREFIX + viewPath);
        String stored = PropertiesComponent.getInstance(project).getValue(design.key);
        if (stored != null)
            for (String line : stored.split("\n")) {
                int tab = line.indexOf('\t');
                if (tab > 0)
                    design.values.put(unescape(line.substring(0, tab)), unescape(line.substring(tab + 1)));
            }
        return design;
    }

    Map<String, String> values() {
        return values;
    }

    void set(String name, String value) {
        values.put(name, value);
        save();
    }

    void clear() {
        values.clear();
        save();
    }

    private void save() {
        StringBuilder text = new StringBuilder();
        for (Map.Entry<String, String> entry : values.entrySet())
            text.append(escape(entry.getKey())).append('\t').append(escape(entry.getValue())).append('\n');
        PropertiesComponent.getInstance(project).setValue(key, text.length() == 0 ? null : text.toString());
    }

    private static String escape(String text) {
        return text.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n");
    }

    private static String unescape(String text) {
        StringBuilder out = new StringBuilder();
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c == '\\' && i + 1 < text.length()) {
                char next = text.charAt(++i);
                out.append(next == 't' ? '\t' : next == 'n' ? '\n' : next);
            } else
                out.append(c);
        }
        return out.toString();
    }
}
