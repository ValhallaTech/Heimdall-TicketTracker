using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace ValhallaHeimdall.BLL.Extensions
{
    [System.AttributeUsage( System.AttributeTargets.All, AllowMultiple = true )]
    public class MaxFileSizeAttribute : ValidationAttribute
    {
        private readonly int maxFileSize;

        public MaxFileSizeAttribute( int maxFileSize ) => this.maxFileSize = maxFileSize;

        protected override ValidationResult IsValid( object value, ValidationContext validationContext )
        {
            // inheritance happening when you see override, extend further then we could normally reach.
            // method name is valid, bass in the object, the value of the property,
            // validationContext is used, passed. by decorating or data
            if ( !( value is IFormFile file ) )
            {
                return ValidationResult.Success;
            }

            return file.Length > this.maxFileSize
                       ? new ValidationResult( this.GetErrorMessage( ) )
                       : ValidationResult.Success;
        }

        public string GetErrorMessage( ) => $"Maximum allowed file size is {this.maxFileSize} bytes.";
    }
}
