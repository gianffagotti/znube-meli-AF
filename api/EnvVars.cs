using System;

namespace meli_znube_integration.Api;

public static class EnvVars
{
    public static class Keys
    {
        public const string MeliBaseUrl = "MELI_BASE_URL";
        public const string ZnubeBaseUrl = "ZNUBE_BASE_URL";

        public const string MeliClientId = "MELI_CLIENT_ID";
        public const string MeliClientSecret = "MELI_CLIENT_SECRET";
        public const string MeliRedirectUri = "MELI_REDIRECT_URI";
        public const string MeliLogisticTypeFlex = "MELI_LOGISTIC_TYPE_FLEX";
        public const string MeliLogisticTypeFull = "MELI_LOGISTIC_TYPE_FULL";
        public const string MeliSellerId = "MELI_SELLER_ID";

        public const string AzureStorageConnectionString = "AZURE_STORAGE_CONNECTION_STRING";
        public const string LocksContainer = "LOCKS_CONTAINER";
        public const string TokensContainer = "TOKENS_CONTAINER";
        public const string TokensBlobName = "TOKENS_BLOB_NAME";

        public const string SendBuyerMessage = "SEND_BUYER_MESSAGE";
        public const string UpsertOrderNote = "UPSERT_ORDER_NOTE";
    }

    public static string? GetString(string key, string? defaultValue = null)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(v)) return defaultValue;
        return v;
    }

    public static string GetRequiredString(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(v))
        {
            throw new InvalidOperationException($"Missing {key}");
        }
        return v!;
    }

    public static bool GetBool(string key, bool defaultValue)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(v))
        {
            // Normalizar por defecto
            Environment.SetEnvironmentVariable(key, defaultValue ? "true" : "false");
            return defaultValue;
        }

        v = v.Trim();
        var isTrue = string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase);
        var isFalse = string.Equals(v, "0", StringComparison.Ordinal)
            || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "n", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "off", StringComparison.OrdinalIgnoreCase);

        bool result;
        if (isTrue) result = true;
        else if (isFalse) result = false;
        else result = defaultValue;

        return result;
    }
}


