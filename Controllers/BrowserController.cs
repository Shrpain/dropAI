using DropAI.Services;
using Microsoft.AspNetCore.Mvc;

namespace DropAI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BrowserController : ControllerBase
    {
        private readonly BrowserService _browserService;

        public BrowserController(BrowserService browserService)
        {
            _browserService = browserService;
        }

        [HttpPost("start")]
        public async Task<IActionResult> Start([FromBody] StartRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return BadRequest("URL is required.");
            }

            await _browserService.StartBrowserAsync(request.Url, request.Username, request.Password);
            return Ok(new { status = _browserService.Status });
        }

        [HttpPost("navigate")]
        public async Task<IActionResult> Navigate([FromBody] StartRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Url)) return BadRequest("URL required");
            await _browserService.NavigateAsync(request.Url);
            await _browserService.NavigateAsync(request.Url);
            return Ok(new { status = _browserService.Status });
        }

        [HttpPost("click")]
        public async Task<IActionResult> Click([FromBody] StartRequest request)
        {
            // Reusing StartRequest for simplicity as it has a generic string field we can use (Url -> Selector)
            // Or create a new DTO. Let's reuse Url as 'Selector' to be quick and simple or add a property.
            // Using 'Url' property to pass the selector string for now.
             if (string.IsNullOrWhiteSpace(request.Url)) return BadRequest("Selector required (passed in Url field)");
            await _browserService.ClickAsync(request.Url);
            return Ok(new { status = _browserService.Status });
        }

        [HttpPost("bet")]
        public async Task<IActionResult> Bet([FromBody] BetRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Type)) return BadRequest("Type required");
            await _browserService.PlaceBetAsync(request.Type, request.Amount);
            return Ok(new { status = _browserService.Status });
        }

        [HttpPost("stop")]
        public async Task<IActionResult> Stop()
        {
            await _browserService.StopBrowserAsync();
            return Ok(new { status = _browserService.Status });
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return Ok(new { status = _browserService.Status });
        }
    }

    public class StartRequest
    {
        public string Url { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    public class BetRequest
    {
        public string Type { get; set; } = "Big";
        public int Amount { get; set; } = 1000;
    }
}
