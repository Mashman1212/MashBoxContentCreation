using UnityEditor;
using UnityEngine;

namespace ContentTools
{
    public class RenameDialog : EditorWindow
    {
        private string _input;
        private System.Action<string> _onConfirm;
        private static RenameDialog _window;

        public static void Show(string title, string message, string defaultText, System.Action<string> onConfirm)
        {
            _window = CreateInstance<RenameDialog>();
            _window.titleContent = new GUIContent(title);
            _window._input = defaultText;
            _window._onConfirm = onConfirm;
            _window.minSize = new Vector2(350, 110);
            _window.ShowUtility();
            _window.message = message;
        }

        private string message;

        private void OnGUI()
        {
            GUILayout.Label(message, EditorStyles.wordWrappedLabel);
            GUI.SetNextControlName("renameText");
            _input = EditorGUILayout.TextField(_input);

            GUILayout.Space(10);
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Cancel")) Close();
                if (GUILayout.Button("OK"))
                {
                    _onConfirm?.Invoke(_input.Trim());
                    Close();
                }
            }

            // Auto-focus input field
            EditorGUI.FocusTextInControl("renameText");
        }
    }
}