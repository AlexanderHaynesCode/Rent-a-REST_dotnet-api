namespace RentARestaurant.Api.Domain;

/// <summary>
/// Rules for an open day's opening and closing times. A closing time earlier than the opening time
/// means the hours run past midnight into the next morning (e.g. 10 AM – 2 AM), which is allowed up
/// to <see cref="LatestOvernightCloseTime"/>. Mirrors the business_hours CHECK constraint.
/// </summary>
public static class BusinessHourRules
{
    /// <summary>The latest closing time allowed when a day's hours run past midnight.</summary>
    public static readonly TimeOnly LatestOvernightCloseTime = new(4, 0);

    public static bool AreTimesValid(TimeOnly openTime, TimeOnly closeTime, bool isClosed) =>
        isClosed ||
        closeTime > openTime ||
        (closeTime < openTime && closeTime <= LatestOvernightCloseTime);

    public static string InvalidTimesMessage(string dayLabel) =>
        $"The closing time for {dayLabel} must be after the opening time, or no later than 4:00 AM when the hours run past midnight.";
}
