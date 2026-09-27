namespace Reacon.Sdk.Client
{
    /// <summary>A CSV HTTP error retaining status, headers and raw body.</summary>
    public sealed class CsvExportException : ApiException
    {
        /// <summary>The completed response, including inspectable headers.</summary>
        public IApiResponse Response { get; }
        /// <summary>Create an error from a completed CSV request.</summary>
        public CsvExportException(IApiResponse response) : base(response.ReasonPhrase, response.StatusCode, response.RawContent) { Response = response; }
    }
}
