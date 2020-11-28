using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ValhallaHeimdall.API.Services
{
    public interface IHeimdallFileService
    {
        public Task<byte[]> ConvertFileToByteArrayAsync( IFormFile file );

        public string ConvertByteArrayToFile( byte[] fileData, string extension );

        public string GetFileIcon( string file );

        public string FormatFileSize( long bytes );
    }
}
