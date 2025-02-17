#nullable disable

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Server.Implementations.LiveTv.Listings.SchedulesDirectDtos
{
    /// <summary>
    /// Lineups dto.
    /// </summary>
    public class LineupsDto
    {
        /// <summary>
        /// Gets or sets the response code.
        /// </summary>
        [JsonPropertyName("code")]
        public int Code { get; set; }

        /// <summary>
        /// Gets or sets the server id.
        /// </summary>
        [JsonPropertyName("serverID")]
        public string ServerId { get; set; }

        /// <summary>
        /// Gets or sets the datetime.
        /// </summary>
        [JsonPropertyName("datetime")]
        public string Datetime { get; set; }

        /// <summary>
        /// Gets or sets the list of lineups.
        /// </summary>
        [JsonPropertyName("lineups")]
        public List<LineupDto> Lineups { get; set; }
    }
}
