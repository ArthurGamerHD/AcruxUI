package acrux.preview;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** The subset of BSON that Avalonia's remote protocol (Metsys.Bson) uses. */
final class Bson {
    private Bson() {
    }

    static byte[] write(Map<String, Object> document) {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        writeDocument(out, document);
        return out.toByteArray();
    }

    private static void writeDocument(ByteArrayOutputStream out, Map<String, ?> document) {
        ByteArrayOutputStream body = new ByteArrayOutputStream();
        for (Map.Entry<String, ?> entry : document.entrySet())
            writeElement(body, entry.getKey(), entry.getValue());
        byte[] bytes = body.toByteArray();
        writeInt(out, bytes.length + 5);
        out.writeBytes(bytes);
        out.write(0);
    }

    private static void writeElement(ByteArrayOutputStream out, String name, Object value) {
        if (value == null) {
            out.write(0x0A);
            writeCString(out, name);
        } else if (value instanceof Double || value instanceof Float) {
            out.write(0x01);
            writeCString(out, name);
            writeLong(out, Double.doubleToLongBits(((Number) value).doubleValue()));
        } else if (value instanceof String) {
            out.write(0x02);
            writeCString(out, name);
            byte[] text = ((String) value).getBytes(StandardCharsets.UTF_8);
            writeInt(out, text.length + 1);
            out.writeBytes(text);
            out.write(0);
        } else if (value instanceof Map) {
            out.write(0x03);
            writeCString(out, name);
            @SuppressWarnings("unchecked") Map<String, ?> map = (Map<String, ?>) value;
            writeDocument(out, map);
        } else if (value instanceof List) {
            out.write(0x04);
            writeCString(out, name);
            Map<String, Object> array = new LinkedHashMap<>();
            List<?> list = (List<?>) value;
            for (int i = 0; i < list.size(); i++)
                array.put(Integer.toString(i), list.get(i));
            writeDocument(out, array);
        } else if (value instanceof byte[]) {
            // Metsys writes byte[] as the old binary subtype (2) with an inner length.
            byte[] bytes = (byte[]) value;
            out.write(0x05);
            writeCString(out, name);
            writeInt(out, bytes.length + 4);
            out.write(2);
            writeInt(out, bytes.length);
            out.writeBytes(bytes);
        } else if (value instanceof Boolean) {
            out.write(0x08);
            writeCString(out, name);
            out.write((Boolean) value ? 1 : 0);
        } else if (value instanceof Long) {
            out.write(0x12);
            writeCString(out, name);
            writeLong(out, (Long) value);
        } else if (value instanceof Integer) {
            out.write(0x10);
            writeCString(out, name);
            writeInt(out, (Integer) value);
        } else {
            throw new IllegalArgumentException("Unsupported BSON value: " + value.getClass());
        }
    }

    static Map<String, Object> read(byte[] data) {
        ByteBuffer buffer = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN);
        return readDocument(buffer);
    }

    private static Map<String, Object> readDocument(ByteBuffer buffer) {
        int start = buffer.position();
        int length = buffer.getInt();
        Map<String, Object> result = new LinkedHashMap<>();
        while (buffer.position() < start + length - 1) {
            int type = buffer.get() & 0xFF;
            String name = readCString(buffer);
            result.put(name, readValue(buffer, type));
        }
        buffer.position(start + length);
        return result;
    }

    private static Object readValue(ByteBuffer buffer, int type) {
        switch (type) {
            case 0x01:
                return buffer.getDouble();
            case 0x02: {
                int length = buffer.getInt();
                byte[] text = new byte[length - 1];
                buffer.get(text);
                buffer.get();
                return new String(text, StandardCharsets.UTF_8);
            }
            case 0x03:
                return readDocument(buffer);
            case 0x04:
                return new ArrayList<>(readDocument(buffer).values());
            case 0x05: {
                int length = buffer.getInt();
                int subtype = buffer.get() & 0xFF;
                if (subtype == 2) {
                    int inner = buffer.getInt();
                    byte[] bytes = new byte[inner];
                    buffer.get(bytes);
                    return bytes;
                }
                byte[] bytes = new byte[length];
                buffer.get(bytes);
                return bytes;
            }
            case 0x08:
                return buffer.get() != 0;
            case 0x0A:
                return null;
            case 0x10:
                return buffer.getInt();
            case 0x12:
                return buffer.getLong();
            default:
                throw new IllegalStateException("Unsupported BSON type 0x" + Integer.toHexString(type));
        }
    }

    private static void writeCString(ByteArrayOutputStream out, String text) {
        out.writeBytes(text.getBytes(StandardCharsets.UTF_8));
        out.write(0);
    }

    private static String readCString(ByteBuffer buffer) {
        int start = buffer.position();
        while (buffer.get() != 0) {
        }
        byte[] bytes = new byte[buffer.position() - start - 1];
        buffer.position(start);
        buffer.get(bytes);
        buffer.get();
        return new String(bytes, StandardCharsets.UTF_8);
    }

    static void writeInt(ByteArrayOutputStream out, int value) {
        out.write(value);
        out.write(value >>> 8);
        out.write(value >>> 16);
        out.write(value >>> 24);
    }

    private static void writeLong(ByteArrayOutputStream out, long value) {
        writeInt(out, (int) value);
        writeInt(out, (int) (value >>> 32));
    }
}
