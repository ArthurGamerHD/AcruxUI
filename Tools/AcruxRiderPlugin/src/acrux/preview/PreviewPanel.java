package acrux.preview;

import java.awt.Color;
import java.awt.Font;
import java.awt.FontMetrics;
import java.awt.Graphics;
import java.awt.Graphics2D;
import java.awt.RenderingHints;
import java.awt.image.BufferedImage;
import javax.swing.JPanel;

/** Shows the latest frame from the preview host, scaled to fit, with a status line. */
final class PreviewPanel extends JPanel {
    private volatile BufferedImage frame;
    private volatile String status = "Starting Acrux preview...";
    private volatile boolean statusIsError;
    /** A warning above the frame (e.g. a build is needed); null when none. Unlike the status, frames keep it. */
    private volatile String notice;

    PreviewPanel() {
        setBackground(new Color(0x1E, 0x1F, 0x22));
    }

    /** The cursor the hovered control asks for (Acrux.Core.Input.PointerCursor). */
    void showCursor(int pointerCursor) {
        int type;
        switch (pointerCursor) {
            case 1: type = java.awt.Cursor.TEXT_CURSOR; break;       // Ibeam
            case 2: type = java.awt.Cursor.WAIT_CURSOR; break;       // Wait
            case 3: type = java.awt.Cursor.CROSSHAIR_CURSOR; break;  // Cross
            case 5: type = java.awt.Cursor.E_RESIZE_CURSOR; break;   // SizeWestEast
            case 6: type = java.awt.Cursor.N_RESIZE_CURSOR; break;   // SizeNorthSouth
            case 7: type = java.awt.Cursor.MOVE_CURSOR; break;       // SizeAll
            case 9: type = java.awt.Cursor.HAND_CURSOR; break;       // Hand
            case 10: type = java.awt.Cursor.WAIT_CURSOR; break;      // AppStarting
            case 12: type = java.awt.Cursor.N_RESIZE_CURSOR; break;  // TopSide
            case 13: type = java.awt.Cursor.S_RESIZE_CURSOR; break;  // BottomSide
            case 14: type = java.awt.Cursor.W_RESIZE_CURSOR; break;  // LeftSide
            case 15: type = java.awt.Cursor.E_RESIZE_CURSOR; break;  // RightSide
            case 16: type = java.awt.Cursor.NW_RESIZE_CURSOR; break; // TopLeftCorner
            case 17: type = java.awt.Cursor.NE_RESIZE_CURSOR; break; // TopRightCorner
            case 18: type = java.awt.Cursor.SW_RESIZE_CURSOR; break; // BottomLeftCorner
            case 19: type = java.awt.Cursor.SE_RESIZE_CURSOR; break; // BottomRightCorner
            case 20: type = java.awt.Cursor.MOVE_CURSOR; break;      // DragMove
            default: type = java.awt.Cursor.DEFAULT_CURSOR; break;
        }
        setCursor(java.awt.Cursor.getPredefinedCursor(type));
    }

    void showFrame(BufferedImage image) {
        frame = image;
        status = null;
        repaint();
    }

    void showNotice(String text) {
        notice = text == null || text.isEmpty() ? null : text;
        repaint();
    }

    void showStatus(String text, boolean error) {
        status = text;
        statusIsError = error;
        repaint();
    }

    /** Where a point of this panel falls on the frame, in frame pixels; null when outside it. */
    double[] toFrame(int x, int y) {
        BufferedImage image = frame;
        if (image == null)
            return null;
        double[] layout = layout(image);
        double fx = (x - layout[0]) / layout[2];
        double fy = (y - layout[1]) / layout[2];
        if (fx < 0 || fy < 0 || fx >= image.getWidth() || fy >= image.getHeight())
            return null;
        return new double[]{fx, fy};
    }

    private double[] layout(BufferedImage image) {
        double scale = Math.min(1.0, Math.min(getWidth() / (double) image.getWidth(), getHeight() / (double) image.getHeight()));
        double w = image.getWidth() * scale, h = image.getHeight() * scale;
        return new double[]{(getWidth() - w) / 2, (getHeight() - h) / 2, scale};
    }

    @Override
    protected void paintComponent(Graphics g) {
        super.paintComponent(g);
        Graphics2D g2 = (Graphics2D) g;
        BufferedImage image = frame;
        if (image != null) {
            double[] l = layout(image);
            g2.setRenderingHint(RenderingHints.KEY_INTERPOLATION, RenderingHints.VALUE_INTERPOLATION_BILINEAR);
            g2.drawImage(image, (int) l[0], (int) l[1], (int) (image.getWidth() * l[2]), (int) (image.getHeight() * l[2]), null);
        }
        String warning = notice;
        if (warning != null) {
            g2.setRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING, RenderingHints.VALUE_TEXT_ANTIALIAS_ON);
            g2.setFont(getFont().deriveFont(Font.PLAIN, 12f));
            FontMetrics metrics = g2.getFontMetrics();
            int boxHeight = metrics.getHeight() + 10;
            g2.setColor(new Color(0x5A, 0x46, 0x10, 230));
            g2.fillRect(0, 0, getWidth(), boxHeight);
            g2.setColor(new Color(0xF2, 0xC9, 0x4C));
            g2.drawString("\u26A0 " + warning, 8, 5 + metrics.getAscent());
        }
        String text = status;
        if (text != null) {
            g2.setRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING, RenderingHints.VALUE_TEXT_ANTIALIAS_ON);
            g2.setFont(getFont().deriveFont(Font.PLAIN, 12f));
            FontMetrics metrics = g2.getFontMetrics();
            String[] lines = text.split("\n");
            int lineHeight = metrics.getHeight();
            int boxHeight = lineHeight * lines.length + 12;
            int y = getHeight() - boxHeight;
            g2.setColor(new Color(0, 0, 0, 190));
            g2.fillRect(0, y, getWidth(), boxHeight);
            g2.setColor(statusIsError ? new Color(0xF2, 0x8B, 0x82) : new Color(0xC8, 0xD0, 0xD8));
            for (int i = 0; i < lines.length; i++)
                g2.drawString(lines[i], 8, y + 6 + metrics.getAscent() + i * lineHeight);
        }
    }
}
