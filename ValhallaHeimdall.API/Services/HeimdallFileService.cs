using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ValhallaHeimdall.API.Services
{
    public class HeimdallFileService : IHeimdallFileService
    {
        private readonly string[] suffixes = { "Bytes", "KB", "MB", "GB", "TB", "PB" };

        public async Task<byte[]> ConvertFileToByteArrayAsync( IFormFile file )
        {
            MemoryStream memoryStream = new MemoryStream( );
            await file
                  .CopyToAsync( memoryStream )
                  .ConfigureAwait( false );

            byte[] byteFile = memoryStream.ToArray( );

            memoryStream.Close( );

            await memoryStream
                  .DisposeAsync( )
                  .ConfigureAwait( false );

            return byteFile;
        }

        public string ConvertByteArrayToFile( byte[] fileData, string extension )
        {
            string imageBase64Data = Convert.ToBase64String( fileData );

            return string.Format( $"data:image/{extension};base64,{imageBase64Data}" );
        }

        public string GetFileIcon( string file )
        {
            string ext = Path.GetExtension( file ).Replace( ".", string.Empty );

            return $"/img/png/{ext}.png";
        }

        public string FormatFileSize( long bytes )
        {
            int     counter = 0;
            decimal number  = bytes;

            while ( Math.Round( number / 1024 ) >= 1 )
            {
                number /= 1024;
                counter++;
            }

            return $"{number:n1}{this.suffixes[counter]}";
        }
    }
}
