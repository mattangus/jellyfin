#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.LiveTv.Listings.SchedulesDirectDtos;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.LiveTv.Listings
{
    public class SchedulesDirect : IListingsProvider
    {
        private const string ApiUrl = "https://json.schedulesdirect.org/20141201";

        private readonly ILogger<SchedulesDirect> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SemaphoreSlim _tokenSemaphore = new SemaphoreSlim(1, 1);
        private readonly IApplicationHost _appHost;
        private readonly ICryptoProvider _cryptoProvider;

        private readonly ConcurrentDictionary<string, NameValuePair> _tokens = new ConcurrentDictionary<string, NameValuePair>();
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
        private DateTime _lastErrorResponse;

        public SchedulesDirect(
            ILogger<SchedulesDirect> logger,
            IHttpClientFactory httpClientFactory,
            IApplicationHost appHost,
            ICryptoProvider cryptoProvider)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _appHost = appHost;
            _cryptoProvider = cryptoProvider;
        }

        /// <inheritdoc />
        public string Name => "Schedules Direct";

        /// <inheritdoc />
        public string Type => nameof(SchedulesDirect);

        private static List<string> GetScheduleRequestDates(DateTime startDateUtc, DateTime endDateUtc)
        {
            var dates = new List<string>();

            var start = new List<DateTime> { startDateUtc, startDateUtc.ToLocalTime() }.Min().Date;
            var end = new List<DateTime> { endDateUtc, endDateUtc.ToLocalTime() }.Max().Date;

            while (start <= end)
            {
                dates.Add(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                start = start.AddDays(1);
            }

            return dates;
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(channelId))
            {
                throw new ArgumentNullException(nameof(channelId));
            }

            // Normalize incoming input
            channelId = channelId.Replace(".json.schedulesdirect.org", string.Empty, StringComparison.OrdinalIgnoreCase).TrimStart('I');

            var token = await GetToken(info, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("SchedulesDirect token is empty, returning empty program list");

                return Enumerable.Empty<ProgramInfo>();
            }

            var dates = GetScheduleRequestDates(startDateUtc, endDateUtc);

            _logger.LogInformation("Channel Station ID is: {ChannelID}", channelId);
            var requestList = new List<RequestScheduleForChannelDto>()
                {
                    new RequestScheduleForChannelDto()
                    {
                        StationId = channelId,
                        Date = dates
                    }
                };

            var requestString = JsonSerializer.Serialize(requestList, _jsonOptions);
            _logger.LogDebug("Request string for schedules is: {RequestString}", requestString);

            using var options = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/schedules");
            options.Content = new StringContent(requestString, Encoding.UTF8, MediaTypeNames.Application.Json);
            options.Headers.TryAddWithoutValidation("token", token);
            using var response = await Send(options, true, info, cancellationToken).ConfigureAwait(false);
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dailySchedules = await JsonSerializer.DeserializeAsync<List<DayDto>>(responseStream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Found {ScheduleCount} programs on {ChannelID} ScheduleDirect", dailySchedules.Count, channelId);

            using var programRequestOptions = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/programs");
            programRequestOptions.Headers.TryAddWithoutValidation("token", token);

            var programsID = dailySchedules.SelectMany(d => d.Programs.Select(s => s.ProgramId)).Distinct();
            programRequestOptions.Content = new StringContent("[\"" + string.Join("\", \"", programsID) + "\"]", Encoding.UTF8, MediaTypeNames.Application.Json);

            using var innerResponse = await Send(programRequestOptions, true, info, cancellationToken).ConfigureAwait(false);
            await using var innerResponseStream = await innerResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var programDetails = await JsonSerializer.DeserializeAsync<List<ProgramDetailsDto>>(innerResponseStream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            var programDict = programDetails.ToDictionary(p => p.ProgramId, y => y);

            var programIdsWithImages = programDetails
                .Where(p => p.HasImageArtwork).Select(p => p.ProgramId)
                .ToList();

            var images = await GetImageForPrograms(info, programIdsWithImages, cancellationToken).ConfigureAwait(false);

            var programsInfo = new List<ProgramInfo>();
            foreach (ProgramDto schedule in dailySchedules.SelectMany(d => d.Programs))
            {
                // _logger.LogDebug("Proccesing Schedule for statio ID " + stationID +
                //              " which corresponds to channel " + channelNumber + " and program id " +
                //              schedule.ProgramId + " which says it has images? " +
                //              programDict[schedule.ProgramId].hasImageArtwork);

                if (images != null)
                {
                    var imageIndex = images.FindIndex(i => i.ProgramId == schedule.ProgramId[..10]);
                    if (imageIndex > -1)
                    {
                        var programEntry = programDict[schedule.ProgramId];

                        var allImages = images[imageIndex].Data ?? new List<ImageDataDto>();
                        var imagesWithText = allImages.Where(i => string.Equals(i.Text, "yes", StringComparison.OrdinalIgnoreCase));
                        var imagesWithoutText = allImages.Where(i => string.Equals(i.Text, "no", StringComparison.OrdinalIgnoreCase));

                        const double DesiredAspect = 2.0 / 3;

                        programEntry.PrimaryImage = GetProgramImage(ApiUrl, imagesWithText, true, DesiredAspect) ??
                                                    GetProgramImage(ApiUrl, allImages, true, DesiredAspect);

                        const double WideAspect = 16.0 / 9;

                        programEntry.ThumbImage = GetProgramImage(ApiUrl, imagesWithText, true, WideAspect);

                        // Don't supply the same image twice
                        if (string.Equals(programEntry.PrimaryImage, programEntry.ThumbImage, StringComparison.Ordinal))
                        {
                            programEntry.ThumbImage = null;
                        }

                        programEntry.BackdropImage = GetProgramImage(ApiUrl, imagesWithoutText, true, WideAspect);

                        // programEntry.bannerImage = GetProgramImage(ApiUrl, data, "Banner", false) ??
                        //    GetProgramImage(ApiUrl, data, "Banner-L1", false) ??
                        //    GetProgramImage(ApiUrl, data, "Banner-LO", false) ??
                        //    GetProgramImage(ApiUrl, data, "Banner-LOT", false);
                    }
                }

                programsInfo.Add(GetProgram(channelId, schedule, programDict[schedule.ProgramId]));
            }

            return programsInfo;
        }

        private static int GetSizeOrder(ImageDataDto image)
        {
            if (int.TryParse(image.Height, out int value))
            {
                return value;
            }

            return 0;
        }

        private static string GetChannelNumber(MapDto map)
        {
            var channelNumber = map.LogicalChannelNumber;

            if (string.IsNullOrWhiteSpace(channelNumber))
            {
                channelNumber = map.Channel;
            }

            if (string.IsNullOrWhiteSpace(channelNumber))
            {
                channelNumber = map.AtscMajor + "." + map.AtscMinor;
            }

            return channelNumber.TrimStart('0');
        }

        private static bool IsMovie(ProgramDetailsDto programInfo)
        {
            return string.Equals(programInfo.EntityType, "movie", StringComparison.OrdinalIgnoreCase);
        }

        private ProgramInfo GetProgram(string channelId, ProgramDto programInfo, ProgramDetailsDto details)
        {
            var startAt = GetDate(programInfo.AirDateTime);
            var endAt = startAt.AddSeconds(programInfo.Duration);
            var audioType = ProgramAudio.Stereo;

            var programId = programInfo.ProgramId ?? string.Empty;

            string newID = programId + "T" + startAt.Ticks + "C" + channelId;

            if (programInfo.AudioProperties != null)
            {
                if (programInfo.AudioProperties.Exists(item => string.Equals(item, "atmos", StringComparison.OrdinalIgnoreCase)))
                {
                    audioType = ProgramAudio.Atmos;
                }
                else if (programInfo.AudioProperties.Exists(item => string.Equals(item, "dd 5.1", StringComparison.OrdinalIgnoreCase)))
                {
                    audioType = ProgramAudio.DolbyDigital;
                }
                else if (programInfo.AudioProperties.Exists(item => string.Equals(item, "dd", StringComparison.OrdinalIgnoreCase)))
                {
                    audioType = ProgramAudio.DolbyDigital;
                }
                else if (programInfo.AudioProperties.Exists(item => string.Equals(item, "stereo", StringComparison.OrdinalIgnoreCase)))
                {
                    audioType = ProgramAudio.Stereo;
                }
                else
                {
                    audioType = ProgramAudio.Mono;
                }
            }

            string episodeTitle = null;
            if (details.EpisodeTitle150 != null)
            {
                episodeTitle = details.EpisodeTitle150;
            }

            var info = new ProgramInfo
            {
                ChannelId = channelId,
                Id = newID,
                StartDate = startAt,
                EndDate = endAt,
                Name = details.Titles[0].Title120 ?? "Unknown",
                OfficialRating = null,
                CommunityRating = null,
                EpisodeTitle = episodeTitle,
                Audio = audioType,
                // IsNew = programInfo.@new ?? false,
                IsRepeat = programInfo.New == null,
                IsSeries = string.Equals(details.EntityType, "episode", StringComparison.OrdinalIgnoreCase),
                ImageUrl = details.PrimaryImage,
                ThumbImageUrl = details.ThumbImage,
                IsKids = string.Equals(details.Audience, "children", StringComparison.OrdinalIgnoreCase),
                IsSports = string.Equals(details.EntityType, "sports", StringComparison.OrdinalIgnoreCase),
                IsMovie = IsMovie(details),
                Etag = programInfo.Md5,
                IsLive = string.Equals(programInfo.LiveTapeDelay, "live", StringComparison.OrdinalIgnoreCase),
                IsPremiere = programInfo.Premiere || (programInfo.IsPremiereOrFinale ?? string.Empty).IndexOf("premiere", StringComparison.OrdinalIgnoreCase) != -1
            };

            var showId = programId;

            if (!info.IsSeries)
            {
                // It's also a series if it starts with SH
                info.IsSeries = showId.StartsWith("SH", StringComparison.OrdinalIgnoreCase) && showId.Length >= 14;
            }

            // According to SchedulesDirect, these are generic, unidentified episodes
            // SH005316560000
            var hasUniqueShowId = !showId.StartsWith("SH", StringComparison.OrdinalIgnoreCase) ||
                !showId.EndsWith("0000", StringComparison.OrdinalIgnoreCase);

            if (!hasUniqueShowId)
            {
                showId = newID;
            }

            info.ShowId = showId;

            if (programInfo.VideoProperties != null)
            {
                info.IsHD = programInfo.VideoProperties.Contains("hdtv", StringComparer.OrdinalIgnoreCase);
                info.Is3D = programInfo.VideoProperties.Contains("3d", StringComparer.OrdinalIgnoreCase);
            }

            if (details.ContentRating != null && details.ContentRating.Count > 0)
            {
                info.OfficialRating = details.ContentRating[0].Code.Replace("TV", "TV-", StringComparison.Ordinal)
                    .Replace("--", "-", StringComparison.Ordinal);

                var invalid = new[] { "N/A", "Approved", "Not Rated", "Passed" };
                if (invalid.Contains(info.OfficialRating, StringComparer.OrdinalIgnoreCase))
                {
                    info.OfficialRating = null;
                }
            }

            if (details.Descriptions != null)
            {
                if (details.Descriptions.Description1000 != null && details.Descriptions.Description1000.Count > 0)
                {
                    info.Overview = details.Descriptions.Description1000[0].Description;
                }
                else if (details.Descriptions.Description100 != null && details.Descriptions.Description100.Count > 0)
                {
                    info.Overview = details.Descriptions.Description100[0].Description;
                }
            }

            if (info.IsSeries)
            {
                info.SeriesId = programId.Substring(0, 10);

                info.SeriesProviderIds[MetadataProvider.Zap2It.ToString()] = info.SeriesId;

                if (details.Metadata != null)
                {
                    foreach (var metadataProgram in details.Metadata)
                    {
                        var gracenote = metadataProgram.Gracenote;
                        if (gracenote != null)
                        {
                            info.SeasonNumber = gracenote.Season;

                            if (gracenote.Episode > 0)
                            {
                                info.EpisodeNumber = gracenote.Episode;
                            }

                            break;
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(details.OriginalAirDate))
            {
                info.OriginalAirDate = DateTime.Parse(details.OriginalAirDate, CultureInfo.InvariantCulture);
                info.ProductionYear = info.OriginalAirDate.Value.Year;
            }

            if (details.Movie != null)
            {
                if (!string.IsNullOrEmpty(details.Movie.Year)
                    && int.TryParse(details.Movie.Year, out int year))
                {
                    info.ProductionYear = year;
                }
            }

            if (details.Genres != null)
            {
                info.Genres = details.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
                info.IsNews = details.Genres.Contains("news", StringComparer.OrdinalIgnoreCase);

                if (info.Genres.Contains("children", StringComparer.OrdinalIgnoreCase))
                {
                    info.IsKids = true;
                }
            }

            return info;
        }

        private static DateTime GetDate(string value)
        {
            var date = DateTime.ParseExact(value, "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

            if (date.Kind != DateTimeKind.Utc)
            {
                date = DateTime.SpecifyKind(date, DateTimeKind.Utc);
            }

            return date;
        }

        private string GetProgramImage(string apiUrl, IEnumerable<ImageDataDto> images, bool returnDefaultImage, double desiredAspect)
        {
            var match = images
                .OrderBy(i => Math.Abs(desiredAspect - GetAspectRatio(i)))
                .ThenByDescending(i => GetSizeOrder(i))
                .FirstOrDefault();

            if (match == null)
            {
                return null;
            }

            var uri = match.Uri;

            if (string.IsNullOrWhiteSpace(uri))
            {
                return null;
            }
            else if (uri.IndexOf("http", StringComparison.OrdinalIgnoreCase) != -1)
            {
                return uri;
            }
            else
            {
                return apiUrl + "/image/" + uri;
            }
        }

        private static double GetAspectRatio(ImageDataDto i)
        {
            int width = 0;
            int height = 0;

            if (!string.IsNullOrWhiteSpace(i.Width))
            {
                _ = int.TryParse(i.Width, out width);
            }

            if (!string.IsNullOrWhiteSpace(i.Height))
            {
                _ = int.TryParse(i.Height, out height);
            }

            if (height == 0 || width == 0)
            {
                return 0;
            }

            double result = width;
            result /= height;
            return result;
        }

        private async Task<List<ShowImagesDto>> GetImageForPrograms(
            ListingsProviderInfo info,
            IReadOnlyList<string> programIds,
            CancellationToken cancellationToken)
        {
            if (programIds.Count == 0)
            {
                return new List<ShowImagesDto>();
            }

            StringBuilder str = new StringBuilder("[", 1 + (programIds.Count * 13));
            foreach (ReadOnlySpan<char> i in programIds)
            {
                str.Append('"')
                    .Append(i.Slice(0, 10))
                    .Append("\",");
            }

            // Remove last ,
            str.Length--;
            str.Append(']');

            using var message = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/metadata/programs")
            {
                Content = new StringContent(str.ToString(), Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            try
            {
                using var innerResponse2 = await Send(message, true, info, cancellationToken).ConfigureAwait(false);
                await using var response = await innerResponse2.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<List<ShowImagesDto>>(response, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting image info from schedules direct");

                return new List<ShowImagesDto>();
            }
        }

        public async Task<List<NameIdPair>> GetHeadends(ListingsProviderInfo info, string country, string location, CancellationToken cancellationToken)
        {
            var token = await GetToken(info, cancellationToken).ConfigureAwait(false);

            var lineups = new List<NameIdPair>();

            if (string.IsNullOrWhiteSpace(token))
            {
                return lineups;
            }

            using var options = new HttpRequestMessage(HttpMethod.Get, ApiUrl + "/headends?country=" + country + "&postalcode=" + location);
            options.Headers.TryAddWithoutValidation("token", token);

            try
            {
                using var httpResponse = await Send(options, false, info, cancellationToken).ConfigureAwait(false);
                await using var response = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                var root = await JsonSerializer.DeserializeAsync<List<HeadendsDto>>(response, _jsonOptions, cancellationToken).ConfigureAwait(false);

                if (root != null)
                {
                    foreach (HeadendsDto headend in root)
                    {
                        foreach (LineupDto lineup in headend.Lineups)
                        {
                            lineups.Add(new NameIdPair
                            {
                                Name = string.IsNullOrWhiteSpace(lineup.Name) ? lineup.Lineup : lineup.Name,
                                Id = lineup.Uri[18..]
                            });
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("No lineups available");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting headends");
            }

            return lineups;
        }

        private async Task<string> GetToken(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            var username = info.Username;

            // Reset the token if there's no username
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            var password = info.Password;
            if (string.IsNullOrEmpty(password))
            {
                return null;
            }

            // Avoid hammering SD
            if ((DateTime.UtcNow - _lastErrorResponse).TotalMinutes < 1)
            {
                return null;
            }

            if (!_tokens.TryGetValue(username, out NameValuePair savedToken))
            {
                savedToken = new NameValuePair();
                _tokens.TryAdd(username, savedToken);
            }

            if (!string.IsNullOrEmpty(savedToken.Name) && !string.IsNullOrEmpty(savedToken.Value))
            {
                if (long.TryParse(savedToken.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out long ticks))
                {
                    // If it's under 24 hours old we can still use it
                    if (DateTime.UtcNow.Ticks - ticks < TimeSpan.FromHours(20).Ticks)
                    {
                        return savedToken.Name;
                    }
                }
            }

            await _tokenSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await GetTokenInternal(username, password, cancellationToken).ConfigureAwait(false);
                savedToken.Name = result;
                savedToken.Value = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                return result;
            }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode.HasValue)
                {
                    if ((int)ex.StatusCode.Value == 400)
                    {
                        _tokens.Clear();
                        _lastErrorResponse = DateTime.UtcNow;
                    }
                }

                throw;
            }
            finally
            {
                _tokenSemaphore.Release();
            }
        }

        private async Task<HttpResponseMessage> Send(
            HttpRequestMessage options,
            bool enableRetry,
            ListingsProviderInfo providerInfo,
            CancellationToken cancellationToken,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
        {
            var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(options, completionOption, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            // Response is automatically disposed in the calling function,
            // so dispose manually if not returning.
            response.Dispose();
            if (!enableRetry || (int)response.StatusCode >= 500)
            {
                throw new HttpRequestException(
                    string.Format(CultureInfo.InvariantCulture, "Request failed: {0}", response.ReasonPhrase),
                    null,
                    response.StatusCode);
            }

            _tokens.Clear();
            options.Headers.TryAddWithoutValidation("token", await GetToken(providerInfo, cancellationToken).ConfigureAwait(false));
            return await Send(options, false, providerInfo, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> GetTokenInternal(
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            using var options = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/token");
            var hashedPasswordBytes = _cryptoProvider.ComputeHash("SHA1", Encoding.ASCII.GetBytes(password), Array.Empty<byte>());
            // TODO: remove ToLower when Convert.ToHexString supports lowercase
            // Schedules Direct requires the hex to be lowercase
            string hashedPassword = Convert.ToHexString(hashedPasswordBytes).ToLowerInvariant();
            options.Content = new StringContent("{\"username\":\"" + username + "\",\"password\":\"" + hashedPassword + "\"}", Encoding.UTF8, MediaTypeNames.Application.Json);

            using var response = await Send(options, false, null, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var root = await JsonSerializer.DeserializeAsync<TokenDto>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            if (string.Equals(root.Message, "OK", StringComparison.Ordinal))
            {
                _logger.LogInformation("Authenticated with Schedules Direct token: {Token}", root.Token);
                return root.Token;
            }

            throw new Exception("Could not authenticate with Schedules Direct Error: " + root.Message);
        }

        private async Task AddLineupToAccount(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            var token = await GetToken(info, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentException("Authentication required.");
            }

            if (string.IsNullOrEmpty(info.ListingsId))
            {
                throw new ArgumentException("Listings Id required");
            }

            _logger.LogInformation("Adding new LineUp ");

            using var options = new HttpRequestMessage(HttpMethod.Put, ApiUrl + "/lineups/" + info.ListingsId);
            options.Headers.TryAddWithoutValidation("token", token);
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default).SendAsync(options, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> HasLineup(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(info.ListingsId))
            {
                throw new ArgumentException("Listings Id required");
            }

            var token = await GetToken(info, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(token))
            {
                throw new Exception("token required");
            }

            _logger.LogInformation("Headends on account ");

            using var options = new HttpRequestMessage(HttpMethod.Get, ApiUrl + "/lineups");
            options.Headers.TryAddWithoutValidation("token", token);

            try
            {
                using var httpResponse = await Send(options, false, null, cancellationToken).ConfigureAwait(false);
                httpResponse.EnsureSuccessStatusCode();
                await using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var response = httpResponse.Content;
                var root = await JsonSerializer.DeserializeAsync<LineupsDto>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);

                return root.Lineups.Any(i => string.Equals(info.ListingsId, i.Lineup, StringComparison.OrdinalIgnoreCase));
            }
            catch (HttpRequestException ex)
            {
                // SchedulesDirect returns 400 if no lineups are configured.
                if (ex.StatusCode.HasValue && ex.StatusCode.Value == HttpStatusCode.BadRequest)
                {
                    return false;
                }

                throw;
            }
        }

        public async Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        {
            if (validateLogin)
            {
                if (string.IsNullOrEmpty(info.Username))
                {
                    throw new ArgumentException("Username is required");
                }

                if (string.IsNullOrEmpty(info.Password))
                {
                    throw new ArgumentException("Password is required");
                }
            }

            if (validateListings)
            {
                if (string.IsNullOrEmpty(info.ListingsId))
                {
                    throw new ArgumentException("Listings Id required");
                }

                var hasLineup = await HasLineup(info, CancellationToken.None).ConfigureAwait(false);

                if (!hasLineup)
                {
                    await AddLineupToAccount(info, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        {
            return GetHeadends(info, country, location, CancellationToken.None);
        }

        public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            var listingsId = info.ListingsId;
            if (string.IsNullOrEmpty(listingsId))
            {
                throw new Exception("ListingsId required");
            }

            var token = await GetToken(info, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(token))
            {
                throw new Exception("token required");
            }

            using var options = new HttpRequestMessage(HttpMethod.Get, ApiUrl + "/lineups/" + listingsId);
            options.Headers.TryAddWithoutValidation("token", token);

            using var httpResponse = await Send(options, true, info, cancellationToken).ConfigureAwait(false);
            await using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var root = await JsonSerializer.DeserializeAsync<ChannelDto>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Found {ChannelCount} channels on the lineup on ScheduleDirect", root.Map.Count);
            _logger.LogInformation("Mapping Stations to Channel");

            var allStations = root.Stations ?? new List<StationDto>();

            var map = root.Map;
            var list = new List<ChannelInfo>(map.Count);
            foreach (var channel in map)
            {
                var channelNumber = GetChannelNumber(channel);

                var station = allStations.Find(item => string.Equals(item.StationId, channel.StationId, StringComparison.OrdinalIgnoreCase))
                    ?? new StationDto
                    {
                        StationId = channel.StationId
                    };

                var channelInfo = new ChannelInfo
                {
                    Id = station.StationId,
                    CallSign = station.Callsign,
                    Number = channelNumber,
                    Name = string.IsNullOrWhiteSpace(station.Name) ? channelNumber : station.Name
                };

                if (station.Logo != null)
                {
                    channelInfo.ImageUrl = station.Logo.Url;
                }

                list.Add(channelInfo);
            }

            return list;
        }

        private static string NormalizeName(string value)
        {
            return value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        }
    }
}
