using System.Security.Cryptography;
using System.Text;

namespace CallAutomation.AzureAI.VoiceLive
{
    public static class Helpers
    {
        public static string ComputeHmac(string data, string key)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
        }

        public static bool FixedTimeEquals(string a, string b)
        {
            var aa = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            return CryptographicOperations.FixedTimeEquals(aa, bb);
        }
    }
}
