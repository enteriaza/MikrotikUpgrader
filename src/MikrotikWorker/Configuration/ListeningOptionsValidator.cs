// -----------------------------------------------------------------------------
// File: ListeningOptionsValidator.cs
//
// Description:
// IValidateOptions for ListeningOptions: parseable IP address and TCP port in
// the range 1–65535.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

/// <summary>
/// Validates <see cref="ListeningOptions"/> at options bind / host start.
/// </summary>
internal sealed class ListeningOptionsValidator : IValidateOptions<ListeningOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ListeningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IPAddress.TryParse(options.IpAddress, out _))
        {
            return ValidateOptionsResult.Fail(
                $"Listening:IpAddress '{options.IpAddress}' is not a valid IP address.");
        }

        if (options.Port is < 1 or > 65535)
        {
            return ValidateOptionsResult.Fail(
                $"Listening:Port '{options.Port}' must be between 1 and 65535.");
        }

        return ValidateOptionsResult.Success;
    }
}
