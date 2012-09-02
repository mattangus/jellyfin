﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.DTO;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Api
{
    /// <summary>
    /// Contains some helpers for the api
    /// </summary>
    public static class ApiService
    {
        public static BaseItem GetItemById(string id)
        {
            Guid guid = string.IsNullOrEmpty(id) ? Guid.Empty : new Guid(id);

            return Kernel.Instance.GetItemById(guid);
        }

        public async static Task<DTOBaseItem> GetDTOBaseItem(BaseItem item, User user,
            bool includeChildren = true,
            bool includePeople = true)
        {
            DTOBaseItem dto = new DTOBaseItem();

            List<Task> tasks = new List<Task>();

            tasks.Add(AttachStudios(dto, item));

            if (includeChildren)
            {
                tasks.Add(AttachChildren(dto, item, user));
                tasks.Add(AttachLocalTrailers(dto, item, user));
            }

            if (includePeople)
            {
                tasks.Add(AttachPeople(dto, item));
            }

            AttachBasicFields(dto, item, user);
            
            // Make sure all the tasks we kicked off have completed.
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            return dto;
        }

        private static void AttachBasicFields(DTOBaseItem dto, BaseItem item, User user)
        {
            dto.AspectRatio = item.AspectRatio;
            dto.BackdropCount = item.BackdropImagePaths == null ? 0 : item.BackdropImagePaths.Count();
            dto.DateCreated = item.DateCreated;
            dto.DisplayMediaType = item.DisplayMediaType;

            if (item.Genres != null)
            {
                dto.Genres = item.Genres.ToArray();
            }
            
            dto.HasArt = !string.IsNullOrEmpty(item.ArtImagePath);
            dto.HasBanner = !string.IsNullOrEmpty(item.BannerImagePath);
            dto.HasLogo = !string.IsNullOrEmpty(item.LogoImagePath);
            dto.HasPrimaryImage = !string.IsNullOrEmpty(item.PrimaryImagePath);
            dto.HasThumb = !string.IsNullOrEmpty(item.ThumbnailImagePath);
            dto.Id = item.Id;
            dto.IsNew = item.IsRecentlyAdded(user);
            dto.IndexNumber = item.IndexNumber;
            dto.IsFolder = item.IsFolder;
            dto.Language = item.Language;
            dto.LocalTrailerCount = item.LocalTrailers == null ? 0 : item.LocalTrailers.Count();
            dto.Name = item.Name;
            dto.OfficialRating = item.OfficialRating;
            dto.Overview = item.Overview;

            // If there are no backdrops, indicate what parent has them in case the UI wants to allow inheritance
            if (dto.BackdropCount == 0)
            {
                int backdropCount;
                dto.ParentBackdropItemId = GetParentBackdropItemId(item, out backdropCount);
                dto.ParentBackdropCount = backdropCount;
            }

            if (item.Parent != null)
            {
                dto.ParentId = item.Parent.Id;
            }

            dto.ParentIndexNumber = item.ParentIndexNumber;

            // If there is no logo, indicate what parent has one in case the UI wants to allow inheritance
            if (!dto.HasLogo)
            {
                dto.ParentLogoItemId = GetParentLogoItemId(item);
            }

            dto.Path = item.Path;

            dto.PremiereDate = item.PremiereDate;
            dto.ProductionYear = item.ProductionYear;
            dto.ProviderIds = item.ProviderIds;
            dto.RunTimeTicks = item.RunTimeTicks;
            dto.SortName = item.SortName;

            if (item.Taglines != null)
            {
                dto.Taglines = item.Taglines.ToArray();
            }

            dto.TrailerUrl = item.TrailerUrl;
            dto.Type = item.GetType().Name;
            dto.UserRating = item.UserRating;

            dto.UserData = item.GetUserData(user);

            Folder folder = item as Folder;

            if (folder != null)
            {
                dto.SpecialCounts = folder.GetSpecialCounts(user);

                dto.IsRoot = folder.IsRoot;
                dto.IsVirtualFolder = folder.IsVirtualFolder;
            }

            Audio audio = item as Audio;

            if (audio != null)
            {
                dto.AudioInfo = new AudioInfo()
                {
                    Album = audio.Album,
                    AlbumArtist = audio.AlbumArtist,
                    Artist = audio.Artist,
                    BitRate = audio.BitRate,
                    Channels = audio.Channels
                };
            }

            Video video = item as Video;

            if (video != null)
            {
                dto.VideoInfo = new VideoInfo()
                {
                    Height = video.Height,
                    Width = video.Width,
                    Codec = video.Codec,
                    VideoType = video.VideoType,
                    ScanType = video.ScanType
                };

                if (video.AudioStreams != null)
                {
                    dto.VideoInfo.AudioStreams = video.AudioStreams.ToArray();
                }

                if (video.Subtitles != null)
                {
                    dto.VideoInfo.Subtitles = video.Subtitles.ToArray();
                }
            }
        }

        private static async Task AttachStudios(DTOBaseItem dto, BaseItem item)
        {
            // Attach Studios by transforming them into BaseItemStudio (DTO)
            if (item.Studios != null)
            {
                Studio[] entities = await Task.WhenAll<Studio>(item.Studios.Select(c => Kernel.Instance.ItemController.GetStudio(c))).ConfigureAwait(false);

                dto.Studios = new BaseItemStudio[entities.Length];

                for (int i = 0; i < entities.Length; i++)
                {
                    Studio entity = entities[i];
                    BaseItemStudio baseItemStudio = new BaseItemStudio();

                    baseItemStudio.Name = entity.Name;

                    baseItemStudio.HasImage = !string.IsNullOrEmpty(entity.PrimaryImagePath);

                    dto.Studios[i] = baseItemStudio;
                }
            }
        }

        private static async Task AttachChildren(DTOBaseItem dto, BaseItem item, User user)
        {
            var folder = item as Folder;

            if (folder != null)
            {
                IEnumerable<BaseItem> children = folder.GetParentalAllowedChildren(user);

                dto.Children = await Task.WhenAll<DTOBaseItem>(children.Select(c => GetDTOBaseItem(c, user, false, false))).ConfigureAwait(false);
            }
        }

        private static async Task AttachLocalTrailers(DTOBaseItem dto, BaseItem item, User user)
        {
            if (item.LocalTrailers != null && item.LocalTrailers.Any())
            {
                dto.LocalTrailers = await Task.WhenAll<DTOBaseItem>(item.LocalTrailers.Select(c => GetDTOBaseItem(c, user, false, false))).ConfigureAwait(false);
            }
        }

        private static async Task AttachPeople(DTOBaseItem dto, BaseItem item)
        {
            // Attach People by transforming them into BaseItemPerson (DTO)
            if (item.People != null)
            {
                IEnumerable<Person> entities = await Task.WhenAll<Person>(item.People.Select(c => Kernel.Instance.ItemController.GetPerson(c.Key))).ConfigureAwait(false);

                dto.People = item.People.Select(p =>
                {
                    BaseItemPerson baseItemPerson = new BaseItemPerson();

                    baseItemPerson.Name = p.Key;
                    baseItemPerson.Overview = p.Value.Overview;
                    baseItemPerson.Type = p.Value.Type;

                    Person ibnObject = entities.First(i => i.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase));

                    if (ibnObject != null)
                    {
                        baseItemPerson.HasImage = !string.IsNullOrEmpty(ibnObject.PrimaryImagePath);
                    }

                    return baseItemPerson;
                }).ToArray();
            }
        }

        private static Guid? GetParentBackdropItemId(BaseItem item, out int backdropCount)
        {
            backdropCount = 0;

            var parent = item.Parent;

            while (parent != null)
            {
                if (parent.BackdropImagePaths != null && parent.BackdropImagePaths.Any())
                {
                    backdropCount = parent.BackdropImagePaths.Count();
                    return parent.Id;
                }

                parent = parent.Parent;
            }

            return null;
        }

        private static Guid? GetParentLogoItemId(BaseItem item)
        {
            var parent = item.Parent;

            while (parent != null)
            {
                if (!string.IsNullOrEmpty(parent.LogoImagePath))
                {
                    return parent.Id;
                }

                parent = parent.Parent;
            }

            return null;
        }

        public static IBNItem GetIBNItem(BaseEntity entity, int itemCount)
        {
            return new IBNItem()
            {
                Id = entity.Id,
                BaseItemCount = itemCount,
                HasImage = !string.IsNullOrEmpty(entity.PrimaryImagePath),
                Name = entity.Name
            };
        }

        public static DTOUser GetDTOUser(User user)
        {
            return new DTOUser()
            {
                Id = user.Id,
                Name = user.Name,
                HasImage = !string.IsNullOrEmpty(user.PrimaryImagePath)
            };
        }
    }
}
