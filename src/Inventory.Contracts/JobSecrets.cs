using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Inventory.Contracts
{
    /// <summary>Öffentlicher Schlüssel, den der Runner für <see cref="StepRunAs.CredentialsUser"/> ins Job-Verzeichnis legt.</summary>
    public sealed class SecretRequest
    {
        public string Modulus { get; set; } = "";
        public string Exponent { get; set; } = "";
        /// <summary>Für welchen Schritt: dessen <see cref="JobStep.CredentialId"/>; null = die Zugangsdaten, mit denen der Server den Rechner erreicht.</summary>
        public int? CredentialId { get; set; }
    }

    /// <summary>Benutzer und Passwort, mit dem Schlüssel aus <see cref="SecretRequest"/> verschlüsselt; je Block ein Base64-Eintrag.</summary>
    public sealed class SecretAnswer
    {
        public List<string> User { get; set; } = new List<string>();
        public List<string> Password { get; set; } = new List<string>();
    }

    /// <summary>
    /// Wie das Passwort für <see cref="StepRunAs.CredentialsUser"/> zum Runner kommt, ohne je im Klartext im Job-Verzeichnis
    /// oder im Netz zu stehen: Der Runner erzeugt ein Schlüsselpaar nur im Speicher und legt den öffentlichen Schlüssel ab
    /// (<see cref="JobFiles.SecretRequest"/>), der Server antwortet verschlüsselt (<see cref="JobFiles.SecretAnswer"/>).
    /// </summary>
    public static class JobSecrets
    {
        public const int KeySize = 2048;

        /// <summary>Ob ein Schritt als Konto der Zugangsdaten läuft: Nur dann bekommt der Runner Benutzer und Passwort.</summary>
        public static bool Needed(JobManifest job) => job.Steps.Any(s => s.RunAs == StepRunAs.CredentialsUser);

        /// <summary>
        /// Beantwortet eine Anfrage des Runners im Job-Verzeichnis (lokal oder über ADMIN$), falls eine neue da ist.
        /// true, wenn geantwortet wurde; false, wenn (noch) keine lesbare, unbeantwortete Anfrage vorliegt.
        /// </summary>
        /// <param name="account">
        /// Das Konto zur Anfrage (<see cref="SecretRequest.CredentialId"/>); null = gehört nicht zu diesem Job, dann bleibt die
        /// Anfrage unbeantwortet und der Runner meldet nach seiner Frist, dass nichts kam.
        /// </param>
        /// <param name="answered">
        /// Schlüssel der zuletzt behandelten Anfrage: Sie liegt noch da, bis der Runner die Antwort gelesen hat, und wird nicht
        /// noch einmal beantwortet. Fragt der Runner nach einem weiteren Konto, bringt er einen neuen Schlüssel mit.
        /// </param>
        public static bool AnswerFile(string jobDir, Func<int?, NetworkCredential?> account, ref string? answered)
        {
            var requestPath = Path.Combine(jobDir, JobFiles.SecretRequest);
            SecretAnswer answer;
            try
            {
                if (!File.Exists(requestPath) || !(JobJson.Deserialize<SecretRequest>(File.ReadAllText(requestPath, Encoding.UTF8)) is { } request)
                    || request.Modulus == answered)
                    return false;
                if (!(account(request.CredentialId) is { } credential))
                {
                    answered = request.Modulus;
                    return false;
                }
                answer = Answer(request, Account(credential), credential.Password);
                answered = request.Modulus;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
            {
                return false;
            }
            // Erst vollständig schreiben, dann umbenennen: Der Runner liest die Antwort, sobald es sie gibt.
            var answerPath = Path.Combine(jobDir, JobFiles.SecretAnswer);
            var temporary = answerPath + ".tmp";
            File.WriteAllText(temporary, JobJson.Serialize(answer), new UTF8Encoding(false));
            if (File.Exists(answerPath))
                File.Delete(answerPath);
            File.Move(temporary, answerPath);
            return true;
        }

        /// <summary>Die Zugangsdaten, nach denen die Schritte eines Jobs fragen können: je <see cref="JobStep.CredentialId"/> einmal, null = die der Verbindung.</summary>
        public static IEnumerable<int?> Accounts(JobManifest job) =>
            job.Steps.Where(s => s.RunAs == StepRunAs.CredentialsUser).Select(s => s.CredentialId).Distinct();

        public static SecretRequest CreateRequest(RSA key)
        {
            var parameters = key.ExportParameters(false);
            return new SecretRequest { Modulus = Convert.ToBase64String(parameters.Modulus), Exponent = Convert.ToBase64String(parameters.Exponent) };
        }

        /// <summary>Verschlüsselt Benutzer und Passwort für den Runner. Läuft auf dem Server (OAEP mit SHA-256 braucht dort kein CNG).</summary>
        /// <exception cref="ArgumentException">Kein brauchbarer Schlüssel in der Anfrage.</exception>
        public static SecretAnswer Answer(SecretRequest request, string userName, string password)
        {
            byte[] modulus, exponent;
            try
            {
                modulus = Convert.FromBase64String(request.Modulus);
                exponent = Convert.FromBase64String(request.Exponent);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("The key of the request is not readable.", nameof(request), ex);
            }
            if (modulus.Length * 8 < KeySize || exponent.Length == 0)
                throw new ArgumentException("The key of the request is too short.", nameof(request));
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
                return new SecretAnswer { User = Encrypt(rsa, userName), Password = Encrypt(rsa, password) };
            }
        }

        /// <summary>Entschlüsselt die Antwort mit dem privaten Schlüssel des Runners.</summary>
        /// <exception cref="CryptographicException">Die Antwort passt nicht zum Schlüssel.</exception>
        public static (string User, string Password) Decrypt(RSA key, SecretAnswer answer) =>
            (Decrypt(key, answer.User), Decrypt(key, answer.Password));

        /// <summary>Der Kontoname, wie ihn <see cref="SplitAccount"/> erwartet, auch wenn die Domäne getrennt hinterlegt ist.</summary>
        public static string Account(NetworkCredential credential) =>
            string.IsNullOrEmpty(credential.Domain) ? credential.UserName : credential.Domain + "\\" + credential.UserName;

        /// <summary>
        /// Zerlegt einen Kontonamen für LogonUser: „DOMÄNE\benutzer“ und „.\benutzer“ in Benutzer und Domäne, „benutzer@domäne“
        /// bleibt ganz (Domäne null), ein Name ohne Domäne ist ein lokales Konto (Domäne „.“), wie beim SMB-Transport.
        /// </summary>
        public static (string User, string? Domain) SplitAccount(string account)
        {
            var slash = account.IndexOf('\\');
            if (slash >= 0)
                return (account.Substring(slash + 1), slash == 0 ? "." : account.Substring(0, slash));
            return account.IndexOf('@') >= 0 ? (account, null) : (account, ".");
        }

        // OAEP mit SHA-256: je Block höchstens Schlüssellänge − 2·32 − 2 Bytes.
        private static int BlockSize(RSA key) => key.KeySize / 8 - 66;

        private static List<string> Encrypt(RSA key, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var size = BlockSize(key);
            var blocks = new List<string>();
            for (var offset = 0; offset < bytes.Length || blocks.Count == 0; offset += size)
            {
                var block = new byte[Math.Min(size, bytes.Length - offset)];
                Array.Copy(bytes, offset, block, 0, block.Length);
                blocks.Add(Convert.ToBase64String(key.Encrypt(block, RSAEncryptionPadding.OaepSHA256)));
            }
            return blocks;
        }

        private static string Decrypt(RSA key, List<string> blocks)
        {
            var bytes = new List<byte>();
            foreach (var block in blocks)
                bytes.AddRange(key.Decrypt(Convert.FromBase64String(block), RSAEncryptionPadding.OaepSHA256));
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
