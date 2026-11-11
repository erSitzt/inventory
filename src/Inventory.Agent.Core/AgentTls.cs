using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Inventory.Contracts;

namespace Inventory.Agent
{
    /// <summary>
    /// Wem der Agent über HTTPS vertraut: einem Zertifikat mit gültiger Kette (Zertifikatspeicher des Systems) oder einem, dessen
    /// Schlüssel zu einem Pin aus agent.json passt – so geht auch das selbstsignierte Zertifikat des Servers. Einen Schalter
    /// „alles annehmen“ gibt es nicht.
    /// </summary>
    public static class AgentTls
    {
        public static HttpClientHandler Handler(IReadOnlyCollection<string>? pins)
        {
            var handler = new HttpClientHandler();
            if (pins != null && pins.Count > 0)
                handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => Accept(certificate, errors, pins);
            return handler;
        }

        public static bool Accept(X509Certificate2? certificate, SslPolicyErrors errors, IReadOnlyCollection<string>? pins) =>
            errors == SslPolicyErrors.None
            || (certificate != null && pins != null && pins.Contains(AgentProtocol.KeyPin(certificate)));
    }
}
