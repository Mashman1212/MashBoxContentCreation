// Editor coroutine

using System.Collections;
using UnityEditor;
using UnityEngine;

namespace ContentTools.Editor
{
    public static class EditorCoroutine
    {
        private class Runner : ScriptableObject
        {
            private IEnumerator _routine;
            public static void Start(IEnumerator routine)
            {
                var r = CreateInstance<Runner>();
                r.hideFlags = HideFlags.HideAndDontSave;
                r._routine = routine;
                EditorApplication.update += r.Step;
            }
            void Step()
            {
                if (_routine == null || !_routine.MoveNext())
                {
                    EditorApplication.update -= Step;
                    DestroyImmediate(this);
                }
            }
        }

        public static void Start(IEnumerator routine)
        {
            if (routine != null) Runner.Start(routine);
        }
    }
}