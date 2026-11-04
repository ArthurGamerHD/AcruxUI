package acrux.preview;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * One design property of the previewed view, as the host reports it: a public static settable property of the view's
 * design data class ("Type.Property"), its kind (string, bool, int, float, enum or color), its current value as text
 * and, for an enum, the names to choose from.
 */
final class DesignProperty {
    final String name;
    final String kind;
    final String value;
    final List<String> options;

    DesignProperty(String name, String kind, String value, List<String> options) {
        this.name = name;
        this.kind = kind;
        this.value = value;
        this.options = options;
    }

    /** The label shown next to the editor: the property's own name. */
    String label() {
        int dot = name.lastIndexOf('.');
        return dot < 0 ? name : name.substring(dot + 1);
    }

    /** Same properties (names, kinds and choices), whatever their values: the editors can stay. */
    static boolean sameShape(List<DesignProperty> a, List<DesignProperty> b) {
        if (a.size() != b.size())
            return false;
        for (int i = 0; i < a.size(); i++) {
            DesignProperty x = a.get(i), y = b.get(i);
            if (!x.name.equals(y.name) || !x.kind.equals(y.kind) || !x.options.equals(y.options))
                return false;
        }
        return true;
    }

    static List<DesignProperty> fromMessage(Map<String, Object> body) {
        List<String> names = strings(body.get("Names"));
        List<String> kinds = strings(body.get("Kinds"));
        List<String> values = strings(body.get("Values"));
        List<String> options = strings(body.get("Options"));
        List<DesignProperty> properties = new ArrayList<>();
        for (int i = 0; i < names.size(); i++) {
            String kind = i < kinds.size() ? kinds.get(i) : "string";
            String value = i < values.size() ? values.get(i) : "";
            String choices = i < options.size() ? options.get(i) : "";
            List<String> list = choices.isEmpty() ? List.of() : List.of(choices.split("\n"));
            properties.add(new DesignProperty(names.get(i), kind, value, list));
        }
        return properties;
    }

    private static List<String> strings(Object value) {
        List<String> list = new ArrayList<>();
        if (value instanceof List)
            for (Object item : (List<?>) value)
                list.add(item == null ? "" : item.toString());
        return list;
    }
}
