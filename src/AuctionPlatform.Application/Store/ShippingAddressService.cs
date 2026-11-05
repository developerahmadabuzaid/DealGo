using System.Text;
using System.Text.RegularExpressions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Store;

public sealed record ShippingAddressDto(
    Guid Id,
    string FullName,
    string Phone,
    string Country,
    string City,
    string? District,
    string Street,
    string? BuildingNumber,
    string? Notes,
    string? ShortAddress,
    double? Latitude,
    double? Longitude,
    bool IsDefault);

/// <summary>What the customer fills in. Every field is checked again here, whatever the app did.</summary>
public sealed record SaveShippingAddressRequest(
    string? FullName,
    string? Phone,
    string? City,
    string? District,
    string? Street,
    string? BuildingNumber = null,
    string? Notes = null,
    string? ShortAddress = null,
    double? Latitude = null,
    double? Longitude = null,
    bool IsDefault = false,
    string? Country = null);

/// <summary>A customer's delivery addresses. Nobody sees or changes another account's.</summary>
public interface IShippingAddressService
{
    Task<IReadOnlyList<ShippingAddressDto>> GetAsync(Guid userId, CancellationToken ct = default);
    Task<ShippingAddressDto> CreateAsync(Guid userId, SaveShippingAddressRequest request, CancellationToken ct = default);

    /// <summary>
    /// Changes an address. One that an order or a prize already went to is archived and replaced
    /// by a new one, so the returned id can differ from <paramref name="addressId"/>.
    /// </summary>
    Task<ShippingAddressDto> UpdateAsync(
        Guid userId, Guid addressId, SaveShippingAddressRequest request, CancellationToken ct = default);

    Task<ShippingAddressDto> SetDefaultAsync(Guid userId, Guid addressId, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid addressId, CancellationToken ct = default);
}

public sealed partial class ShippingAddressService : IShippingAddressService
{
    public const string SaudiArabia = "SA";

    /// <summary>Plenty for home, work and family; a ceiling so the list stays a list.</summary>
    public const int MaxAddressesPerUser = 20;

    // A box around the Kingdom, generous at the edges. A pin outside it is a mistake or a forgery.
    private const double MinLatitude = 16.0, MaxLatitude = 32.5;
    private const double MinLongitude = 34.4, MaxLongitude = 55.7;

    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;

    public ShippingAddressService(IApplicationDbContext db, IDateTimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<ShippingAddressDto>> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var addresses = await _db.ShippingAddresses
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.ArchivedAt == null)
            .OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        return addresses.Select(Map).ToList();
    }

    public async Task<ShippingAddressDto> CreateAsync(
        Guid userId, SaveShippingAddressRequest request, CancellationToken ct = default)
    {
        var fields = Validate(request);

        var active = await _db.ShippingAddresses
            .Where(a => a.UserId == userId && a.ArchivedAt == null)
            .ToListAsync(ct);

        if (active.Count >= MaxAddressesPerUser)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed, $"An account can keep up to {MaxAddressesPerUser} addresses.");
        }

        var address = new ShippingAddress { UserId = userId, CreatedAt = _clock.UtcNow };
        fields.ApplyTo(address);

        // The first address is the default whether or not it was asked for: there is nothing else.
        address.IsDefault = request.IsDefault || active.Count == 0;
        if (address.IsDefault) ClearDefault(active);

        _db.ShippingAddresses.Add(address);
        await _db.SaveChangesAsync(ct);

        return Map(address);
    }

    public async Task<ShippingAddressDto> UpdateAsync(
        Guid userId, Guid addressId, SaveShippingAddressRequest request, CancellationToken ct = default)
    {
        var fields = Validate(request);
        var address = await FindOwnAsync(userId, addressId, ct);
        var now = _clock.UtcNow;

        // Default can be given but not taken away here: removing it would leave the list without one.
        var makeDefault = request.IsDefault || address.IsDefault;
        if (makeDefault && !address.IsDefault)
        {
            ClearDefault(await _db.ShippingAddresses
                .Where(a => a.UserId == userId && a.ArchivedAt == null && a.IsDefault)
                .ToListAsync(ct));
        }

        if (await IsInUseAsync(address.Id, ct))
        {
            // What was sent where stays as it was: the old row is retired and a new one takes its place.
            address.ArchivedAt = now;
            address.IsDefault = false;

            var replacement = new ShippingAddress { UserId = userId, CreatedAt = address.CreatedAt, UpdatedAt = now };
            fields.ApplyTo(replacement);
            replacement.IsDefault = makeDefault;
            _db.ShippingAddresses.Add(replacement);

            await _db.SaveChangesAsync(ct);
            return Map(replacement);
        }

        fields.ApplyTo(address);
        address.IsDefault = makeDefault;
        address.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return Map(address);
    }

    public async Task<ShippingAddressDto> SetDefaultAsync(Guid userId, Guid addressId, CancellationToken ct = default)
    {
        var address = await FindOwnAsync(userId, addressId, ct);

        ClearDefault(await _db.ShippingAddresses
            .Where(a => a.UserId == userId && a.ArchivedAt == null && a.IsDefault && a.Id != addressId)
            .ToListAsync(ct));

        address.IsDefault = true;
        await _db.SaveChangesAsync(ct);

        return Map(address);
    }

    public async Task DeleteAsync(Guid userId, Guid addressId, CancellationToken ct = default)
    {
        var address = await FindOwnAsync(userId, addressId, ct);
        var wasDefault = address.IsDefault;

        if (await IsInUseAsync(address.Id, ct))
        {
            address.ArchivedAt = _clock.UtcNow;
            address.IsDefault = false;
        }
        else
        {
            _db.ShippingAddresses.Remove(address);
        }

        // Another address takes over as the default, so checkout always has one to suggest.
        if (wasDefault)
        {
            var next = await _db.ShippingAddresses
                .Where(a => a.UserId == userId && a.ArchivedAt == null && a.Id != addressId)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (next is not null) next.IsDefault = true;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Refuses an address that is not this customer's, or that they have removed. Checkout and
    /// prize claims call this before attaching an address, so an id guessed or copied from
    /// another account can never route a delivery.
    /// </summary>
    internal static async Task EnsureUsableAsync(
        IApplicationDbContext db, Guid userId, Guid addressId, CancellationToken ct)
    {
        var usable = await db.ShippingAddresses
            .AnyAsync(a => a.Id == addressId && a.UserId == userId && a.ArchivedAt == null, ct);

        if (!usable) throw new DomainException(ErrorCodes.AddressNotFound, "Address not found.");
    }

    internal static ShippingAddressDto Map(ShippingAddress a) => new(
        a.Id, a.FullName, a.Phone, a.Country, a.City, a.District, a.Street, a.BuildingNumber,
        a.Notes, a.ShortAddress, a.Latitude, a.Longitude, a.IsDefault);

    private async Task<ShippingAddress> FindOwnAsync(Guid userId, Guid addressId, CancellationToken ct) =>
        // Someone else's address answers exactly like a missing one: its existence is not revealed.
        await _db.ShippingAddresses
            .FirstOrDefaultAsync(a => a.Id == addressId && a.UserId == userId && a.ArchivedAt == null, ct)
        ?? throw new DomainException(ErrorCodes.AddressNotFound, "Address not found.");

    /// <summary>Whether an order or a prize was already sent, or is going, to this address.</summary>
    private async Task<bool> IsInUseAsync(Guid addressId, CancellationToken ct) =>
        await _db.StoreOrders.AnyAsync(o => o.ShippingAddressId == addressId, ct)
        || await _db.PrizeClaims.AnyAsync(c => c.ShippingAddressId == addressId, ct);

    private static void ClearDefault(IEnumerable<ShippingAddress> addresses)
    {
        foreach (var a in addresses) a.IsDefault = false;
    }

    // ---- validation

    private sealed record Fields(
        string FullName, string Phone, string Country, string City, string District, string Street,
        string? BuildingNumber, string? Notes, string? ShortAddress, double? Latitude, double? Longitude)
    {
        public void ApplyTo(ShippingAddress a)
        {
            a.FullName = FullName;
            a.Phone = Phone;
            a.Country = Country;
            a.City = City;
            a.District = District;
            a.Street = Street;
            a.BuildingNumber = BuildingNumber;
            a.Notes = Notes;
            a.ShortAddress = ShortAddress;
            a.Latitude = Latitude;
            a.Longitude = Longitude;
        }
    }

    private static Fields Validate(SaveShippingAddressRequest r)
    {
        if (r is null) throw Invalid("The address is missing.");

        var country = Clean(r.Country)?.ToUpperInvariant() ?? SaudiArabia;
        if (country is not (SaudiArabia or "SAU"))
        {
            throw Invalid("Deliveries are made inside Saudi Arabia only.");
        }

        var name = Required(r.FullName, "recipient name", 100);
        if (name.Length < 2) throw Invalid("Enter the recipient's name.");

        var phone = SaudiPhone.Normalise(r.Phone);
        var city = Required(r.City, "city", 80);
        var district = Required(r.District, "district", 80);
        var street = Required(r.Street, "street", 150);
        var building = Optional(r.BuildingNumber, "building", 30);
        var notes = Optional(r.Notes, "notes", 500);
        var shortAddress = NormaliseShortAddress(r.ShortAddress);

        if (r.Latitude.HasValue != r.Longitude.HasValue)
        {
            throw Invalid("A location needs both a latitude and a longitude.");
        }

        if (r.Latitude is { } lat && r.Longitude is { } lng)
        {
            if (!double.IsFinite(lat) || !double.IsFinite(lng)
                || lat < MinLatitude || lat > MaxLatitude || lng < MinLongitude || lng > MaxLongitude)
            {
                throw Invalid("The location on the map must be inside Saudi Arabia.");
            }

            // About a metre of precision is all a courier needs, and all that is worth storing.
            lat = Math.Round(lat, 6);
            lng = Math.Round(lng, 6);

            return new Fields(name, phone, SaudiArabia, city, district, street, building, notes,
                shortAddress, lat, lng);
        }

        return new Fields(name, phone, SaudiArabia, city, district, street, building, notes,
            shortAddress, null, null);
    }

    /// <summary>
    /// The National Address short code: four letters then four digits ("RRRD2929"). People type
    /// it with spaces, in lower case or with Arabic digits, so all of those are accepted.
    /// </summary>
    internal static string? NormaliseShortAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var sb = new StringBuilder(8);
        foreach (var c in raw.Trim())
        {
            if (char.IsWhiteSpace(c) || c == '-') continue;
            sb.Append(char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : char.ToUpperInvariant(c));
        }

        var code = sb.ToString();
        if (!ShortAddressPattern().IsMatch(code))
        {
            throw Invalid("The short address is four letters and four digits, for example RRRD2929.");
        }

        return code;
    }

    [GeneratedRegex("^[A-Z]{4}[0-9]{4}$")]
    private static partial Regex ShortAddressPattern();

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Runs of spaces and line breaks collapse: they only make the label on the parcel harder to read.
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static string Required(string? value, string field, int max) =>
        Optional(value, field, max) ?? throw Invalid($"The {field} is required.");

    private static string? Optional(string? value, string field, int max)
    {
        var clean = Clean(value);
        if (clean is not null && clean.Length > max)
        {
            throw Invalid($"The {field} can be at most {max} characters.");
        }

        return clean;
    }

    private static DomainException Invalid(string message) => new(ErrorCodes.ValidationFailed, message);
}
