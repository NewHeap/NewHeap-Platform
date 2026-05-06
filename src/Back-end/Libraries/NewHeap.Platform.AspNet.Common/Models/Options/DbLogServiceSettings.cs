namespace NewHeap.Platform.AspNet.Common.Models.Options;

public class DbLogServiceSettings
{
    /// <summary>
    ///     The directory where to store files which will be relatively referenced from the database entry
    /// </summary>
    public string RootDirectory { get; set; } = "";

    /// <summary>
    ///     Email addresses to mail when an uncaught error occurs
    /// </summary>
    public string[] ErrorMailAddresses { get; set; } = [];

    public bool AdditionalDataProcessingEnabled { get; set; } = true;

    public int AdditionalDataProcessingIntervalInSeconds { get; set; } = 5;

    public int AdditionalDataProcessingBatchSize { get; set; } = 100;
}
