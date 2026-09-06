using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PassCrate.Core.Legal;
using PassCrate.WebSite.Models;
namespace PassCrate.WebSite.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
    [HttpGet("/privacy")]
    [HttpGet("/Home/Privacy")]
    public IActionResult Privacy() => View(new[] { LegalDocuments.Privacy, LegalDocuments.Terms });
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
