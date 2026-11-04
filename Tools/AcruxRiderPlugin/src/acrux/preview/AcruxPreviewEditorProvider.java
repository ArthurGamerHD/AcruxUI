package acrux.preview;

import com.intellij.openapi.fileEditor.FileEditor;
import com.intellij.openapi.fileEditor.FileEditorPolicy;
import com.intellij.openapi.fileEditor.FileEditorProvider;
import com.intellij.openapi.fileEditor.TextEditor;
import com.intellij.openapi.fileEditor.TextEditorWithPreview;
import com.intellij.openapi.fileEditor.impl.text.TextEditorProvider;
import com.intellij.openapi.project.DumbAware;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.vfs.VirtualFile;
import java.nio.charset.StandardCharsets;

/** Opens Acrux views (root element in the Acrux XML namespace) in a text + live preview split editor. */
public final class AcruxPreviewEditorProvider implements FileEditorProvider, DumbAware {
    static final String ACRUX_NAMESPACE = "https://github.com/ArthurGamerHD/AcruxUI";

    @Override
    public boolean accept(Project project, VirtualFile file) {
        String extension = file.getExtension();
        if (extension == null || !(extension.equalsIgnoreCase("axaml") || extension.equalsIgnoreCase("xaml")))
            return false;
        try {
            byte[] bytes = file.contentsToByteArray();
            String head = new String(bytes, 0, Math.min(bytes.length, 4096), StandardCharsets.UTF_8);
            return head.contains("xmlns=\"" + ACRUX_NAMESPACE + "\"");
        } catch (Exception error) {
            return false;
        }
    }

    @Override
    public FileEditor createEditor(Project project, VirtualFile file) {
        TextEditor text = (TextEditor) TextEditorProvider.getInstance().createEditor(project, file);
        return new TextEditorWithPreview(text, new AcruxPreviewEditor(project, file), "Acrux Preview");
    }

    @Override
    public String getEditorTypeId() {
        return "acrux-preview-split-editor";
    }

    @Override
    public FileEditorPolicy getPolicy() {
        return FileEditorPolicy.HIDE_OTHER_EDITORS; // Rider's own XAML editor cannot preview Acrux views
    }
}
