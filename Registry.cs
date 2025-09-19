using System.Collections.Concurrent;
using Azure.Communication.CallAutomation;

namespace CallAutomation.AzureAI.VoiceLive
{
    public interface ICallConnectionRegistry
    {
        void Set(string key, CallConnection conn);
        bool TryGet(string key, out CallConnection conn);
        void Remove(string key);
    }

    public sealed class CallConnectionRegistry : ICallConnectionRegistry
    {
        private readonly ConcurrentDictionary<string, CallConnection> _map = new();
        public void Set(string key, CallConnection conn) => _map[key] = conn;
        public bool TryGet(string key, out CallConnection conn) => _map.TryGetValue(key, out conn!);
        public void Remove(string key) => _map.TryRemove(key, out _);
    }
}
