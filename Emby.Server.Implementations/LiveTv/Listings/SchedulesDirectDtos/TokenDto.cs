#nullable disable

using System.Text.Json.Serialization;

namespace Emby.Server.Implementations.LiveTv.Listings.SchedulesDirectDtos
{
    /// <summary>
    /// The token dto.
    /// </summary>
    public class TokenDto
    {
        /// <summary>
        /// Gets or sets the response code.
        /// </summary>
        [JsonPropertyName("code")]
        public int Code { get; set; }

        /// <summary>
        /// Gets or sets the response message.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; }

        /// <summary>
        /// Gets or sets the server id.
        /// </summary>
        [JsonPropertyName("serverID")]
        public string ServerId { get; set; }

        /// <summary>
        /// Gets or sets the token.
        /// </summary>
        [JsonPropertyName("token")]
        public string Token { get; set; }
    }
}
