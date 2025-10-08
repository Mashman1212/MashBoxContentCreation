// ModIoAuth.cs (proxy-based email flow)
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using ContentTools.Editor;
using UnityEditor;
using UnityEngine;

namespace ContentTools.ModIo
{
    public static class ModIoAuth
    {
        // Selected game's mod.io base that you already save in EditorPrefs when "Set Game" is clicked
        static string ApiBase   => EditorPrefs.GetString("ModIo.ApiBase", "https://api.mod.io/v1");

        // Your backend base (set this once in your tool init, or via EditorPrefs)
        static string ProxyBase => EditorPrefs.GetString("ModIo.ProxyBase", "https://YOUR_BACKEND/modio");

        // per-game token/email storage (namespaced by api base)
        static string TK(string apiBase) => $"modio_access_token::{apiBase}";
        static string EK(string apiBase) => $"modio_email::{apiBase}";

        public static string CurrentToken => EditorPrefs.GetString(TK(ApiBase), "");
        public static string CurrentEmail => EditorPrefs.GetString(EK(ApiBase), "");
        public static bool   IsAuthorizedForCurrentGame() => !string.IsNullOrEmpty(CurrentToken);

        public static void ClearForCurrentGame() { EditorPrefs.DeleteKey(TK(ApiBase)); EditorPrefs.DeleteKey(EK(ApiBase)); }

        public static void BeginEmailRequest(string email, Action<string> onStatus)
            => EditorCoroutine.Start(CoProxyEmailRequest(email, onStatus));

        public static void ExchangeCode(string email, string code, Action<string> onStatus)
            => EditorCoroutine.Start(CoProxyExchange(email, code, onStatus));

        // --- proxy calls (no api_key on the client) ---
        static IEnumerator CoProxyEmailRequest(string email, Action<string> onStatus)
        {
            if (string.IsNullOrEmpty(email)) { onStatus?.Invoke("❌ Missing email."); yield break; }
            onStatus?.Invoke("Requesting login code…");
  
            string currentGame = EditorPrefs.GetString("ModIo.CurrentGame", "BMXS");
            var form = new List<KeyValuePair<string, string>> {
                new("email", email)
            };
            
            Debug.Log($"[mod.io][Auth] Sending EmailRequest via proxy: " +
                      $"ProxyBase={ProxyBase}, CurrentGame={EditorPrefs.GetString("ModIo.CurrentGame", "")}");
            
            var task = ModioHttp.PostUrlEncodedAsync($"{ProxyBase}/emailrequest?game={currentGame}", form);
            while (!task.IsCompleted) yield return null;
            onStatus?.Invoke(task.Result.Contains("error") ? $"❌ {task.Result}" : "✅ Code sent! Check your email.");
        }

        static IEnumerator CoProxyExchange(string email, string code, Action<string> onStatus)
        {
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(code)) { onStatus?.Invoke("❌ Missing email or code."); yield break; }
            onStatus?.Invoke("Exchanging code for token…");
            string currentGame = EditorPrefs.GetString("ModIo.CurrentGame", "BMXS");
            var form = new List<KeyValuePair<string, string>> {
                new("email", email),
                new("security_code", code),
            };
            
            Debug.Log($"[mod.io][Auth] Sending Code Exchange via proxy: " +
                      $"ProxyBase={ProxyBase}, CurrentGame={EditorPrefs.GetString("ModIo.CurrentGame", "")}");

            
            var task = ModioHttp.PostUrlEncodedAsync($"{ProxyBase}/emailexchange?game={currentGame}", form);
            while (!task.IsCompleted) yield return null;

            // Expect your backend to return the raw mod.io JSON with access_token
            string json  = task.Result;
            string token = ExtractJsonValue(json, "access_token");
            if (!string.IsNullOrEmpty(token))
            {
                EditorPrefs.SetString(TK(ApiBase), token);
                EditorPrefs.SetString(EK(ApiBase), email);
                onStatus?.Invoke("✅ Connected to mod.io for this game!");
            }
            else onStatus?.Invoke($"❌ Failed: {json}");
        }

        // tiny JSON value extractor
        static string ExtractJsonValue(string json, string key)
        {
            var marker = $"\"{key}\":";
            int i = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null; i += marker.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\"')) i++;
            int start = i; while (i < json.Length && json[i] != '\"' && json[i] != ',' && json[i] != '}') i++;
            return json.Substring(start, i - start).Trim('\"');
        }
    }
}
#endif
