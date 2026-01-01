using DropAI.Services;
using Microsoft.AspNetCore.Mvc;

namespace DropAI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BrowserController : ControllerBase
    {
        private readonly GameApiService _gameApiService;

        public BrowserController(GameApiService gameApiService)
        {
            _gameApiService = gameApiService;
        }

        [HttpPost("start")]
        public async Task<IActionResult> Start([FromBody] StartRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest("Username and Password are required for API login.");
            }
            // Perform login
            var loginSuccess = await _gameApiService.LoginAsync(request.Username, request.Password);
            if (!loginSuccess)
            {
                return BadRequest("Login failed through Game API.");
            }

            return Ok(new { status = "success", message = "Browser session started and Game API logged in." });
        }

        [HttpPost("navigate")]
        public IActionResult Navigate([FromBody] StartRequest request)
        {
             // Detect ID from URL (e.g. ...id=1)
             int urlId = 1;
             if (!string.IsNullOrEmpty(request.Url))
             {
                 try {
                     var uri = new Uri(request.Url);
                     var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                     if(int.TryParse(query["id"], out var idVal)) urlId = idVal;
                     else if (uri.Fragment.Contains("id=")) {
                        var frag = uri.Fragment.Split('?');
                        if(frag.Length > 1) {
                            var q2 = System.Web.HttpUtility.ParseQueryString(frag[1]);
                            if(int.TryParse(q2["id"], out var idVal2)) urlId = idVal2;
                        }
                     }
                 } catch {}
             }

             int apiTypeId = 30;
             switch(urlId) {
                case 1: apiTypeId = 30; break;
                case 2: apiTypeId = 31; break;
                case 3: apiTypeId = 32; break;
                case 4: apiTypeId = 33; break;
             }

             _gameApiService.CurrentGameId = apiTypeId;
             return Ok(new { status = $"Switched to Game ID: {apiTypeId}" });
        }

        [HttpPost("click")]
        public IActionResult Click([FromBody] StartRequest request)
        {
            // Dummy for API mode
            return Ok(new { status = "Click ignored in API mode" });
        }

        [HttpPost("bet")]
        public async Task<IActionResult> Bet([FromBody] BetRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Type)) return BadRequest("Type required");
            var result = await _gameApiService.PlaceBetAsync(request.Type, request.Amount, _gameApiService.CurrentGameId);
            if (result.Success)
            {
                return Ok(new { status = $"Bet Placed: {request.Type} {request.Amount}", issue = result.IssueNumber });
            }
            else
            {
                return BadRequest(new { status = "Bet Failed", error = result.ErrorMessage });
            }
        }

        [HttpPost("stop")]
        public IActionResult Stop()
        {
            _gameApiService.StopPolling();
            return Ok(new { status = "Polling Stopped" });
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return Ok(new { status = _gameApiService.Status });
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
