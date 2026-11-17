using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// Beantwortet eine Anfrage des Runners im Job-Verzeichnis (lokal oder über ADMIN$), falls eine da ist.
        /// true, wenn geantwortet wurde; false, wenn (noch) keine lesbare Anfrage vorliegt.
        /// </summary>
        public static bool AnswerFile(string jobDir, string userName, string password)
        {
            var requestPath = Path.Combine(jobDir, JobFiles.SecretRequest);
            SecretAnswer answer;
            try
            {
                if (!File.Exists(requestPath) || !(JobJson.Deserialize<SecretRequest>(File.ReadAllText(requestPath, Encoding.UTF8)) is { } request))
                    return false;
                answer = Answer(request, userName, password);
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
        public static string Account(System.Net.NetworkCredential credential) =>
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
