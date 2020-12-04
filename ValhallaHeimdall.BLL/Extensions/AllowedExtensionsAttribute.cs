using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace ValhallaHeimdall.BLL.Extensions
{
    [AttributeUsage( AttributeTargets.All )]
    public class AllowedExtensionsAttribute : ValidationAttribute
    {
        private readonly string[] extensions;

        public AllowedExtensionsAttribute( string[] extensions ) => this.extensions = extensions;

        protected override ValidationResult IsValid( object value, ValidationContext validationContext )
        {
            if ( value is IFormFile file )
            {
                string? extension = Path.GetExtension( file.FileName );

                if ( !this.extensions.Contains( extension.ToLower( ) ) )
                {
                    return new ValidationResult( this.GetErrorMessage( extension ) );
                }
            }

            return ValidationResult.Success;
        }

        public string GetErrorMessage( string ext ) => $"The file extension {ext} is not allowed!";
    }
}

//overloading is two methods with the same name but different signature
//we are extending this to do new things with classes
//using maxFileSize;
