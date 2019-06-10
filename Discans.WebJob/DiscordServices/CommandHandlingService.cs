﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Threading.Tasks;
using Discans.Modules;
using Discans.Shared.Database;
using Discans.Shared.DiscordServices.CrawlerSites;
using Discans.Shared.Models;
using Discans.Shared.Services;
using Discans.WebJob.Resources;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;

namespace Discans.WebJob.Services
{
    public class CommandHandlingService
    {
        private readonly DiscordSocketClient discord;
        private readonly CommandService commands;
        private readonly MangaService mangaService;
        private readonly UserAlertService userAlertService;
        private readonly ChannelService channelService;
        private readonly ServerAlertService serverAlertService;
        private readonly MangaUpdatesCrawlerService mangaUpdatesService;
        private readonly TuMangaCrawlerService tuMangaService;
        private readonly IServiceProvider provider;
        private readonly AppDbContext dbContext;
        private readonly ResourceManager resourceManager;

        public CommandHandlingService(
            IServiceProvider provider, 
            DiscordSocketClient discord,
            CommandService commands, 
            MangaService mangaService,
            UserAlertService userAlertService,
            ChannelService channelService,
            ServerAlertService serverAlertService,
            MangaUpdatesCrawlerService mangaUpdatesService,
            TuMangaCrawlerService tuMangaService,
            AppDbContext dbContext)
        {
            this.discord = discord;
            this.commands = commands;
            this.mangaService = mangaService;
            this.userAlertService = userAlertService;
            this.channelService = channelService;
            this.serverAlertService = serverAlertService;
            this.mangaUpdatesService = mangaUpdatesService;
            this.tuMangaService = tuMangaService;
            this.provider = provider;
            this.dbContext = dbContext;

            this.discord.Connected += Update;
            resourceManager = new ResourceManager(typeof(WebJobResource));
        }

        public async Task InitializeAsync(IServiceProvider provider) =>
            await commands.AddModulesAsync(Assembly.GetEntryAssembly(), provider);

        private Task Update()
        {
            LastReleases().GetAwaiter().GetResult();
            return Task.FromResult(Program.CloseProgram = true);
        }

        private async Task LastReleases()
        {
            var mangas = await mangaService.GetAll();
            var mangaUpdatesReleases = mangaUpdatesService.LastMangaReleases().GroupBy(x => x.MangaSiteId);
            var tuMangasReleases = tuMangaService.LastMangaReleases().GroupBy(x => x.MangaSiteId);

            UserLocalizerService.Languages = await dbContext.UserLocalizer.ToDictionaryAsync(x => x.UserId, x => x.Language);
            ServerLocalizerService.Languages = await dbContext.ServerLocalizer.ToDictionaryAsync(x => x.ServerId, x => x.Language);

            var allSitesRelease = new List<IEnumerable<IGrouping<int, Manga>>>()
            {
                mangaUpdatesReleases,
                tuMangasReleases
            };

            foreach (var siteRelease in allSitesRelease)
            {
                foreach (var manga in mangas.Where(x =>
                    x.MangaSite == siteRelease.First().First().MangaSite &&
                    siteRelease.Any(y => y.Key == x.MangaSiteId)))
                {
                    var mangaSiteRelease = siteRelease.First(x => x.Key == manga.MangaSiteId).First();
                    if (manga.LastRelease == mangaSiteRelease.LastRelease)
                        continue;

                    await mangaService.UpdateLastRelease(manga.Id, mangaSiteRelease.LastRelease);

                    foreach (var serverAlert in manga.ServerAlerts.GroupBy(x => x.ServerId))
                    {
                        await SendServerMessage("@everyone", manga.Name, mangaSiteRelease.LastRelease, serverAlert.Key, manga.MangaSiteId, manga.MangaSite);
                    }

                    foreach (var userAlert in manga.UserAlerts.GroupBy(x => x.ServerId))
                    {
                        var users = string.Join(", ", userAlert.Select(x => $"<@{x.UserId}>"));
                        await SendServerMessage(users, manga.Name, mangaSiteRelease.LastRelease, userAlert.Key, manga.MangaSiteId, manga.MangaSite);
                    }

                    foreach (var privateAlert in manga.PrivateAlerts)
                    {
                        await SendPrivateMessage(privateAlert.UserId, manga.Name, manga.LastRelease, manga.MangaSiteId, manga.MangaSite);
                    }
                }
            }
        }

        private async Task SendPrivateMessage(ulong userId, string mangaName, string lastRelease, int mangaSiteId, MangaSite mangaSite)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(UserLocalizerService.Languages.FirstOrDefault(x => x.Key == userId).Value ?? "en-US");
                var message = string.Format(
                    resourceManager.GetString(nameof(WebJobResource.NewRelease), culture),
                    mangaName,
                    lastRelease,
                    mangaSite.ToString());

                if (mangaSite == MangaSite.TuManga)
                    message += Environment.NewLine + 
                               string.Format(resourceManager.GetString(
                                      nameof(WebJobResource.ReadOnline), culture), 
                                      "https://tmofans.com/library/manga/{mangaSiteId}/discans");

                var channel = await discord.GetUser(userId).GetOrCreateDMChannelAsync();
                await channel.SendMessageAsync(message);
            }
            catch (Exception e) when (e.Message.Contains("error 50007")  // Discord doc: Cannot send messages to this user
                                   || e.Message.Contains("error 10013")) // Discord doc: Unknown user
            {
                // We will not send messages under those conditions. 
                // It occurs when a user blocks Discans Bot, or when a user account is deleted.
            }
            catch(Exception e)
            {
                // todo: log
            }
        }

        private async Task SendServerMessage(string user, string mangaName, string lastRelease, ulong serverId, int mangaSiteId, MangaSite mangaSite)
        {

            var culture = CultureInfo.GetCultureInfo(ServerLocalizerService.Languages.FirstOrDefault(x => x.Key == serverId).Value ?? "en-US");
            var message = user + 
                          Environment.NewLine + 
                          string.Format(resourceManager.GetString(
                                nameof(WebJobResource.NewRelease), culture),
                                mangaName,
                                lastRelease,
                                mangaSite.ToString());

            if (mangaSite == MangaSite.TuManga)
                message += Environment.NewLine +
                           string.Format(resourceManager.GetString(
                                  nameof(WebJobResource.ReadOnline), culture),
                                  "https://tmofans.com/library/manga/{mangaSiteId}/discans");

            if (discord.GetGuild(serverId) == null)
            {
                await userAlertService.Remove(serverId);
                await serverAlertService.Remove(serverId);
            }

            var dbChannel = await channelService.GetByServerId(serverId);                

            try
            {
                if (dbChannel != null)
                {
                    await (discord
                        .GetGuild(serverId)
                        .GetChannel(dbChannel.ChannelId) as SocketTextChannel)
                        .SendMessageAsync(message);
                }
                else
                {
                    message += Environment.NewLine + Environment.NewLine + string.Format(resourceManager.GetString(
                                nameof(WebJobResource.NoChannel), culture),
                                $"{Consts.BotCommand}{ConfigureModule.ChannelCommand}");

                    await discord
                        .GetGuild(serverId)
                        .DefaultChannel
                        .SendMessageAsync(message);
                }

                return;
            }
            catch
            { }

            foreach(var channel in discord.GetGuild(serverId).TextChannels)
            {
                try
                {
                    await channel.SendMessageAsync(message);
                    return;
                }
                catch
                { }
            }

            // todo: save notification to send again - discord server error maybe
        }
    }
}