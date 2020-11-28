using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ValhallaHeimdall.BLL.Models;

namespace ValhallaHeimdall.API.Controllers
{
    // Namespace is the outermost , Inside is a class, than a method, than the logic
    [Authorize]
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> logger;

        public HomeController( ILogger<HomeController> logger ) => this.logger = logger;

        public IActionResult Index( ) => View( );

        [AllowAnonymous]
        public IActionResult LandingPage( ) => View( );

        public IActionResult Privacy( ) => View( );

        [ResponseCache( Duration = 0, Location = ResponseCacheLocation.None, NoStore = true )]
        public IActionResult Error( ) => this.View( new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier } );
    }
}
