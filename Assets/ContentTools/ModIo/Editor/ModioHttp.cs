using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;

namespace ContentTools.ModIo
{
    public static class ModioHttp
    {
        // Shared HttpClient instance across all requests
        private static readonly HttpClient client = new HttpClient();

        static ModioHttp()
        {
            // Force TLS 1.2+ for mod.io connections
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
        }

        // ---------- Standard multipart/form-data POST (used for mod creation, file upload) ----------
        public static async Task<string> PostAsync(string url, MultipartFormDataContent form, string token = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(token))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                using var resp = await client.PostAsync(url, form);
                string body = await resp.Content.ReadAsStringAsync();
                Debug.Log($"[mod.io][HttpClient] POST (multipart) {url} -> {(int)resp.StatusCode}");
                return body;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[mod.io][HttpClient] Exception: {ex}");
                return ex.Message;
            }
        }

        // ---------- application/x-www-form-urlencoded POST (used for login endpoints) ----------
        public static async Task<string> PostUrlEncodedAsync(string url, IEnumerable<KeyValuePair<string, string>> values)
        {
            try
            {
                using var client = new HttpClient();
                using var content = new FormUrlEncodedContent(values);

                // Detailed logging of what’s being sent
                Debug.Log($"[mod.io][HttpClient] ▶️ POST {url}");
                foreach (var kv in values)
                    Debug.Log($"    {kv.Key} = {kv.Value}");

                var response = await client.PostAsync(url, content);
                var txt = await response.Content.ReadAsStringAsync();

                Debug.Log($"[mod.io][HttpClient] ⬅️ Response ({(int)response.StatusCode}): {txt}");
                response.EnsureSuccessStatusCode();
                return txt;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[mod.io][HttpClient] ❌ Exception while POSTing {url}\n{ex}");
                throw;
            }
        }


        // ---------- Basic GET (diagnostic connectivity test) ----------
        public static async Task<bool> TestConnection()
        {
            try
            {
                using var resp = await client.GetAsync("https://api.mod.io/v1/games");
                Debug.Log($"[mod.io][HttpClient] Status {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return resp.IsSuccessStatusCode || (int)resp.StatusCode == 401;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[mod.io][HttpClient] Exception: {ex}");
                return false;
            }
        }
    }
}
