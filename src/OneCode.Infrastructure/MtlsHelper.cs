using System.Security.Cryptography.X509Certificates;

namespace OneCode.Infrastructure;

/// <summary>
/// mTLS helper — loads client certificate/key from environment variables
/// and configures <see cref="HttpClientHandler"/> with mutual TLS.
/// </summary>
public static class MtlsHelper
{
    /// <summary>Environment var pointing to a PKCS#12 (.pfx) client certificate file.</summary>
    private const string ClientCertEnvVar = "ONECODE_CLIENT_CERT";

    /// <summary>Environment var with the passphrase for the PKCS#12 file.</summary>
    private const string ClientCertPassphraseEnvVar = "ONECODE_CLIENT_KEY_PASSPHRASE";

    /// <summary>Environment var pointing to an additional CA certificate bundle (PEM/DER).</summary>
    private const string ExtraCaCertsEnvVar = "NODE_EXTRA_CA_CERTS";

    /// <summary>Load and configure mTLS on the given handler. Returns true when mTLS was applied.</summary>
    public static bool ConfigureMtls(HttpClientHandler handler)
    {
        var certPath = Environment.GetEnvironmentVariable(ClientCertEnvVar);
        if (string.IsNullOrEmpty(certPath) || !File.Exists(certPath))
            return false;

        var passphrase = Environment.GetEnvironmentVariable(ClientCertPassphraseEnvVar);

        try
        {
            var certBytes = File.ReadAllBytes(certPath);
            var cert = string.IsNullOrEmpty(passphrase)
                ? X509CertificateLoader.LoadPkcs12(certBytes, null, X509KeyStorageFlags.DefaultKeySet)
                : X509CertificateLoader.LoadPkcs12(certBytes, passphrase, X509KeyStorageFlags.DefaultKeySet);

            handler.ClientCertificates.Add(cert);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"mTLS: Failed to load client certificate: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Add extra CA certificates to the handler's trust store.
    /// Reads from NODE_EXTRA_CA_CERTS env var.
    /// </summary>
    public static bool ConfigureExtraCaCerts(HttpClientHandler handler)
    {
        var caPath = Environment.GetEnvironmentVariable(ExtraCaCertsEnvVar);
        if (string.IsNullOrEmpty(caPath) || !File.Exists(caPath))
            return false;

        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            var pemContent = File.ReadAllText(caPath);
            var certs = LoadCertificatesFromPem(pemContent);

            foreach (var cert in certs)
            {
                store.Add(cert);
            }

            store.Close();
            return certs.Count > 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"mTLS: Failed to load extra CA certs: {ex.Message}");
            return false;
        }
    }

    /// <summary>Apply both mTLS and extra CA certs to an HttpClientHandler.</summary>
    public static void ApplyToHandler(HttpClientHandler handler)
    {
        if (Environment.GetEnvironmentVariable(ClientCertEnvVar) is { } certPath && !string.IsNullOrEmpty(certPath))
        {
            if (!ConfigureMtls(handler))
                Console.Error.WriteLine($"mTLS: Client certificate configured but failed to load from '{certPath}'. Requests will proceed without mTLS.");
        }

        if (Environment.GetEnvironmentVariable(ExtraCaCertsEnvVar) is { } caPath && !string.IsNullOrEmpty(caPath))
        {
            if (!ConfigureExtraCaCerts(handler))
                Console.Error.WriteLine($"mTLS: Extra CA certs configured but failed to load from '{caPath}'. Handler will use system trust store only.");
        }
    }

    private static List<X509Certificate2> LoadCertificatesFromPem(string pemContent)
    {
        List<X509Certificate2> certs = [];
        var certDelimiter = "-----END CERTIFICATE-----";
        var parts = pemContent.Split(certDelimiter, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var pemBlock = part.Trim() + certDelimiter;
            try
            {
                var cert = X509Certificate2.CreateFromPem(pemBlock.AsSpan());
                certs.Add(cert);
            }
            catch
            {
                // Skip invalid PEM blocks
            }
        }

        return certs;
    }
}
