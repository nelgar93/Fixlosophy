namespace Fixlosophy.Services;

/// <summary>
/// Turns a phone number as a customer typed it into links a phone can actually act on.
/// </summary>
/// <remarks>
/// Shared because the same two links appear against a booking and against an enquiry,
/// and they lived as private helpers on the one page that happened to need them first.
/// Numbers reach here loosely formatted — "07700 900000", "+44 (0)7700 900000" — which
/// is deliberate: rejecting the way people write phone numbers would be worse than
/// normalising them here.
/// </remarks>
public static class ContactLinks
{
    /// <summary>
    /// A number in the form wa.me wants: international, no '+', no leading zeros.
    /// </summary>
    /// <remarks>
    /// A bare national number — one leading zero — is assumed to be UK, which is right
    /// for a bike shop in London and wrong nowhere it's currently used. "00" is the
    /// international prefix and is simply dropped.
    /// </remarks>
    public static string WhatsAppNumber(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal)) return digits[2..];
        if (digits.StartsWith('0')) return "44" + digits[1..];
        return digits;
    }

    /// A tel: href — digits and a leading '+' only, since punctuation people write
    /// between digits means nothing to a dialler.
    public static string Tel(string phone) =>
        $"tel:{new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray())}";

    /// <summary>
    /// A WhatsApp link pre-filled with a message naming the booking, so the customer
    /// knows which appointment is being asked about before they read a word.
    /// </summary>
    public static string WhatsApp(Booking booking)
    {
        var message =
            $"Hi {booking.CustomerName}, this is {SiteContent.ShopName} about your booking " +
            $"{booking.Reference} ({booking.ServiceName}) on {booking.SlotDate:dddd d MMMM} at {booking.SlotTime}.";
        return $"https://wa.me/{WhatsAppNumber(booking.CustomerPhone)}?text={Uri.EscapeDataString(message)}";
    }
}
