﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CommunicaptionBackend.Api;
using CommunicaptionBackend.Core;
using CommunicaptionBackend.Entities;
using CommunicaptionBackend.Messages;
using Newtonsoft.Json;

namespace CommunicaptionBackend.Api {

    public class MainService {
        private readonly MainContext mainContext;
        private readonly MessageQueue messageQueue;
        private readonly MessageProcessor messageProcessor;
        private readonly LuceneProcessor luceneProcessor;

        private const string RECOMMENDER_HOST = "http://37.148.210.36:5005";

        public MainService(MainContext mainContext, MessageProcessor messageProcessor, MessageQueue messageQueue, LuceneProcessor luceneProcessor) {
            this.mainContext = mainContext;
            this.messageProcessor = messageProcessor;
            this.messageQueue = messageQueue;
            this.luceneProcessor = luceneProcessor;
        }

        public void DisconnectDevice(int userId) {
            var user = mainContext.Users.SingleOrDefault(x => userId == x.Id);
            if (user == null)
                return;
            user.Connected = false;
            mainContext.SaveChanges();
        }

        public string GeneratePin() {
            var user = new UserEntity {
                Pin = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 8),
                Connected = false
            };

            mainContext.Users.Add(user);
            mainContext.SaveChanges();
            return user.Pin;
        }

        public byte[] GetMediaData(string mediaId) {
            return File.ReadAllBytes("medias/" + mediaId);
        }

        public string getSearchResult(string searchInputJson) {
            var artList = luceneProcessor.getArtList(mainContext.Texts.ToList());
            luceneProcessor.AddToTheIndex(artList);
            return luceneProcessor.FetchResults(searchInputJson);
        }

        public List<object> GetMediaItems(int userId) {
            List<object> itemInformations = new List<object>();
            var medias = mainContext.Medias.Where(x => x.UserId == userId);
            foreach (var media in medias) {
                object obj = new {
                    mediaId = media.Id,
                    fileName = Newtonsoft.Json.JsonConvert.SerializeObject(media.DateTime),
                    thumbnail = File.ReadAllBytes("thumbnails/" + media.Id.ToString() + ".jpg")
                };
                itemInformations.Add(obj);
            }
            return itemInformations;
        }

        public void PushMessage(Message message) {
            if (message is SaveMediaMessage) {
                messageProcessor.ProcessMessage(message);
            }
            else if (message is SaveTextMessage) {
                messageProcessor.ProcessMessage(message);
            }
            else if (message is SettingsChangedMessage) {
                messageProcessor.ProcessMessage(message);
            }
            else {
                messageQueue.PushMessage(message);
            }
        }

        public List<Message> GetMessages(int userId) {
            return messageQueue.SwapQueue(userId);
        }

        public int CheckForPairing(string pin) {
            var user = mainContext.Users.SingleOrDefault(x => x.Pin == pin);
            if (user == null)
                return 0;
            return user.Id;
        }

        public bool CheckUserExists(int userId) {
            var user = mainContext.Users.SingleOrDefault(x => x.Id == userId);
            if (user == null)
                return false;
            return true;
        }

        public int ConnectWithoutHoloLens() {
            UserEntity user = new UserEntity {
                Pin = Guid.NewGuid().ToString().Replace("-", "").Substring(0, 8),
                Connected = false
            };

            mainContext.Users.Add(user);
            mainContext.SaveChanges();

            return user.Id;
        }

        public int ConnectWithHoloLens(string pin) {
            int userId = CheckForPairing(pin);

            if (userId == 0)
                return 0;
            else {
                var user = mainContext.Users.SingleOrDefault(x => userId == x.Id);
                user.Connected = true;

                mainContext.Users.Update(user);
                mainContext.SaveChanges();

                return userId;
            }
        }

        public string GetUserSettings(int userId) {
            var settings = mainContext.Settings.FirstOrDefault(x => x.UserId == userId);
            if (settings == null)
                return JsonConvert.SerializeObject(new SettingsChangedMessage());
            return settings.Json;
        }

        public object GetGallery(int userId) {
            var artIds = mainContext.Arts.Where(x => x.UserId == userId || x.UserId == 0)
                .Select(x => x.Id).ToArray();

            var mediaIds = new List<int>();
            var textIds = new List<int>();

            foreach (var artId in artIds) {
                mediaIds.AddRange(mainContext.Medias.Where(x => x.ArtId == artId).Select(x => x.Id).ToArray());
                textIds.AddRange(mainContext.Texts.Where(x => x.ArtId == artId).Select(x => x.Id).ToArray());
            }

            mediaIds = mediaIds.Distinct().ToList();
            textIds = textIds.Distinct().ToList();

            var medias = mainContext.Medias.Where(x => mediaIds.Contains(x.Id)).ToList();
            var texts = mainContext.Texts.Where(x => textIds.Contains(x.Id)).ToList();

            var all = new List<dynamic>();
            all.AddRange(medias);
            all.AddRange(texts);
            all.Sort((x, y) => {
                return y.DateTime.CompareTo(x.DateTime);
            });

            return all;
        }

        public int CreateArt(int userId, string artTitle) {
            var art = new ArtEntity();
            art.Title = artTitle;
            art.UserId = userId;

            mainContext.Arts.Add(art);
            mainContext.SaveChanges();
            return art.Id;
        }

        private int[] ArtsSimilar(string title) {
            title = title.ToLowerInvariant();
            var res = new List<int>();
            foreach (var item in mainContext.Arts.Select(x => new { x.Id, x.Title }).ToList()) {
                string t = item.Title.ToLowerInvariant();
                if (t.Contains(title)) {
                    res.Add(item.Id);
                }
            }
            return res.ToArray();
        }

        public void TriggerTrain() {
            var web = new WebClient();
            web.Proxy = null;
            web.Headers[HttpRequestHeader.ContentType] = "application/json";

            var arts = mainContext.Arts.Select(x => new { x.Id, x.UserId, x.Title });

            var ratings = new List<int[]>();
            foreach (var item in arts) {
                ratings.Add(new int[2] { item.UserId, item.Id });
            }
            web.UploadString($"{RECOMMENDER_HOST}/ch1/train/", "POST", JsonConvert.SerializeObject(ratings));

            var docs = mainContext.Texts.Select(x => new { x.Id, x.Text }).ToList();
            web.UploadString($"{RECOMMENDER_HOST}/ch2/train/", "POST", JsonConvert.SerializeObject(new {
                Item1 = docs.Select(x => x.Id).ToArray(),
                Item2 = docs.Select(x => x.Text).ToArray(),
            }));
        }

        public int[] Recommend(int userId, int baseArtId) {
            var artTitle = mainContext.Arts.FirstOrDefault(x => x.Id == baseArtId)?.Title;
            if (artTitle == null) artTitle = ".";

            var web = new WebClient();
            web.Proxy = null;
            web.Headers[HttpRequestHeader.ContentType] = "application/json";


            var channel1 = JsonConvert.DeserializeObject<int[]>(web.DownloadString($"{RECOMMENDER_HOST}/ch1/recommend/{userId}/{baseArtId}"));
            var channel2 = JsonConvert.DeserializeObject<int[]>(web.DownloadString($"{RECOMMENDER_HOST}/ch2/similarity/{userId}"));
            var alsoSearch = JsonConvert.DeserializeObject<string[]>(web.UploadString($"{RECOMMENDER_HOST}/ch3/predict", "POST", artTitle));
            var channel3 = alsoSearch.Select(x => ArtsSimilar(x));

            var all = new List<int>();
            all.AddRange(channel1);
            all.AddRange(channel2);
            foreach (var item in channel3) {
                all.AddRange(item);
            }

            return all.Distinct().ToArray();
        }

        public string TrainDebug() {
            var web = new WebClient();
            web.Proxy = null;
            web.Headers[HttpRequestHeader.ContentType] = "application/json";
            return web.DownloadString($"{RECOMMENDER_HOST}/ch1/info");
        }
    }
}
