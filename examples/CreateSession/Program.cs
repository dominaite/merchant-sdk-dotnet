using Dominaite.MerchantSdk;

// Opens one checkout session against whatever environment DOMINAITE_BASE_URL points at.
//
//   export DOMINAITE_KEY_ID=dmk_...
//   export DOMINAITE_SECRET=dms_...
//   export DOMINAITE_BASE_URL=https://api.staging.dominaite.com/payments   # required
//   dotnet run --project examples/CreateSession
//
// DOMINAITE_BASE_URL is required: the sandbox above, or https://api.dominaite.com/payments for
// production. A sandbox key against production is a guaranteed INVALID_API_KEY - keys are issued
// per environment.

var keyId = Environment.GetEnvironmentVariable("DOMINAITE_KEY_ID");
var secret = Environment.GetEnvironmentVariable("DOMINAITE_SECRET");

var baseUrl = Environment.GetEnvironmentVariable("DOMINAITE_BASE_URL");

if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(baseUrl))
{
    Console.Error.WriteLine("Set DOMINAITE_KEY_ID, DOMINAITE_SECRET and DOMINAITE_BASE_URL first.");
    return 1;
}

using var client = new DominaiteClient(keyId, secret, new DominaiteClientOptions
{
    BaseUrl = baseUrl,
});

try
{
    // First live call: proves key, secret, signing and clock without creating anything.
    var ping = await client.PingAsync();
    Console.WriteLine($"merchant {ping.MerchantId}, clock skew {ping.ClockSkewSeconds}s");

    var request = new CheckoutSessionRequest
    {
        Amount = 2500, // 2500 = 25.00 EUR, always MINOR units
        Currency = "EUR",
        OrderReference = "order-1042",

        // Required. Derived from the order, so a reload or a retry replays this same session.
        IdempotencyKey = IdempotencyKeys.ForOrder("checkout", "order-1042", 2500, "EUR"),

        // Pass everything you already know - prefilled fields are hidden from the payer, so the
        // checkout form stays short.
        Customer = new Customer
        {
            FirstName = "Ana",
            LastName = "Kirova",
            Email = "ana@example.com",
            Address = "12 Vitosha Blvd",
            City = "Sofia",
            PostalCode = "1000",
        },
        Language = "bg",
        Theme = "dark",
    };

    var session = await client.CreateCheckoutSessionAsync(request);

    // Store TransactionId against your order, then hand CashierKey and CashierToken to the page
    // that renders the widget. Never log the token: it is a bearer credential for the session.
    Console.WriteLine($"transaction {session.TransactionId}");
    Console.WriteLine($"cashier key {session.CashierKey}");
    Console.WriteLine("cashier token received (not printed)");

    return 0;
}
catch (DominaiteRefusalException error)
{
    // Machine-readable. A replay refusal names the transaction it collided with.
    Console.Error.WriteLine($"Refused ({error.Code}): {error.Message}");
    if (error.TransactionId is { } collided)
    {
        var status = await client.GetStatusAsync(collided);
        Console.Error.WriteLine($"The earlier attempt is {status.Status}.");
    }

    return 2;
}
catch (DominaiteAuthException error)
{
    Console.Error.WriteLine($"Authentication failed ({error.Code}): {error.Message}");
    return 3;
}
catch (DominaiteTransportException error)
{
    // The client already retried it with the SAME idempotency key. Retry later with that key.
    Console.Error.WriteLine($"Temporarily unavailable: {error.Message}");
    return 4;
}
