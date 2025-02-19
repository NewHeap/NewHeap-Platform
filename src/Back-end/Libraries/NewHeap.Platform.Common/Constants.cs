namespace NewHeap.Platform.Common;

public enum CRUDActionType
{
    Unknown = 0,
    Create = 1,
    Update = 2,
    Delete = 3
}

public static partial class Constants
{
    public static class DateTimeOffset
    {
        public const string StringFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";
    }

    public static class PermissionClaimValues
    {
        public const string AuthenticatedAccess = "nh.platform.access";
    }

    public static class DivisionPermissionClaimValues
    {
        public const string AccessAll = "nh.general.access-all";
        public const string GeneralView = "nh.general.view";
    }
}