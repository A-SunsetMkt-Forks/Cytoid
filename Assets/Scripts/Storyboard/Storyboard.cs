using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cytoid.Storyboard.Controllers;
using Cytoid.Storyboard.Lines;
using Cytoid.Storyboard.Notes;
using Cytoid.Storyboard.Sprites;
using Cytoid.Storyboard.Texts;
using Cytoid.Storyboard.Videos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Cytoid.Storyboard
{
    public class Storyboard
    {
        public Game Game { get; }
        public StoryboardRenderer Renderer { get; }
        public StoryboardConfig Config { get; }
        
        public readonly JObject RootObject;
        
        public readonly Dictionary<string, Text> Texts = new Dictionary<string, Text>();
        public readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();
        public readonly Dictionary<string, Controller> Controllers = new Dictionary<string, Controller>();
        public readonly Dictionary<string, NoteController> NoteControllers = new Dictionary<string, NoteController>();
        public readonly Dictionary<string, Line> Lines = new Dictionary<string, Line>();
        public readonly Dictionary<string, Video> Videos = new Dictionary<string, Video>();
        public readonly List<Trigger> Triggers = new List<Trigger>();
        
        public readonly Dictionary<string, JObject> Templates = new Dictionary<string, JObject>();

        public Storyboard(Game game, string content)
        {
            Game = game;
            Renderer = new StoryboardRenderer(this);
            Config = new StoryboardConfig(this);
            
            UnitFloat.Storyboard = this;
            RootObject = JObject.Parse(content);
            /*JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            };*/ // Moved to Context.cs
        }

        public void Parse()
        {
            if ((bool?) RootObject["compiled"] == true)
            {
                // Directly load into memory
                ((JArray) RootObject["texts"]).Select(it => it.ToObject<Text>()).ForEach(it => Texts[it.Id] = it);
                ((JArray) RootObject["sprites"]).Select(it => it.ToObject<Sprite>()).ForEach(it => Sprites[it.Id] = it);
                ((JArray) RootObject["videos"]).Select(it => it.ToObject<Video>()).ForEach(it => Videos[it.Id] = it);
                ((JArray) RootObject["lines"]).Select(it => it.ToObject<Line>()).ForEach(it => Lines[it.Id] = it);
                ((JArray) RootObject["controllers"]).Select(it => it.ToObject<Controller>()).ForEach(it => Controllers[it.Id] = it);
                ((JArray) RootObject["note_controllers"]).Select(it => it.ToObject<NoteController>()).ForEach(it => NoteControllers[it.Id] = it);
            }
            else
            {
                // Parse
                
                // Templates
                if (RootObject["templates"] != null)
                {
                    foreach (var templateProperty in RootObject["templates"].Children<JProperty>())
                    {
                        Templates[templateProperty.Name] = templateProperty.Value.ToObject<JObject>();
                    }
                }

                void ParseStateObjects<TO, TS>(string rootTokenName, Dictionary<string, TO> addToDictionary,
                    Action<JObject> tokenPreprocessor = null)
                    where TO : Object<TS>, new() where TS : ObjectState, new()
                {
                    if (RootObject[rootTokenName] == null) return;
                    foreach (var childToken in (JArray) RootObject[rootTokenName])
                    {
                        foreach (var objectToken in PopulateJObjects((JObject) childToken))
                        {
                            tokenPreprocessor?.Invoke(objectToken);
                            var obj = LoadObject<TO, TS>(objectToken);
                            if (obj != null)
                            {
                                if (addToDictionary.ContainsKey(obj.Id))
                                {
                                    Debug.LogError($"Storyboard: Redefinition of element {obj.Id}");
                                    continue;
                                }
                                addToDictionary[obj.Id] = obj;
                            }
                        }
                    }
                }

                var timer = new BenchmarkTimer("Storyboard parsing");
                ParseStateObjects<Text, TextState>("texts", Texts);
                timer.Time("Text");
                ParseStateObjects<Sprite, SpriteState>("sprites", Sprites);
                timer.Time("Sprite");
                ParseStateObjects<Video, VideoState>("videos", Videos);
                timer.Time("Videos");
                ParseStateObjects<Line, LineState>("lines", Lines);
                timer.Time("Lines");
                ParseStateObjects<NoteController, NoteControllerState>("note_controllers", NoteControllers, token =>
                {
                    // Note controllers have time default to zero
                    if (token["time"] == null) token["time"] = 0;
                });
                timer.Time("NoteController");
                ParseStateObjects<Controller, ControllerState>("controllers", Controllers, token =>
                {
                    // Controllers have time default to zero
                    if (token["time"] == null) token["time"] = 0;
                });
                timer.Time("Controller");
                timer.Time();

                // Trigger
                if (RootObject["triggers"] != null)
                    foreach (var objectToken in (JArray) RootObject["triggers"])
                        Triggers.Add(LoadTrigger(objectToken));
            }
        }

        public void Dispose()
        {
            Texts.Clear();
            Sprites.Clear();
            Controllers.Clear();
            NoteControllers.Clear();
            Triggers.Clear();
            Templates.Clear();
            Game.onGameLateUpdate.RemoveListener(Renderer.OnGameUpdate);
        }

        public async UniTask Initialize()
        {
            await Renderer.Initialize();
            // Register note clear listener for triggers
            Game.onNoteClear.AddListener(OnNoteClear);
            Game.onGameDisposed.AddListener(_ => Dispose());
            Game.onGameLateUpdate.AddListener(Renderer.OnGameUpdate);
        }

        public void OnNoteClear(Game game, Note note)
        {
            foreach (var trigger in Triggers)
            {
                if (trigger.Type == TriggerType.NoteClear && trigger.Notes.Contains(note.Model.id))
                {
                    trigger.Triggerer = note;
                    OnTrigger(trigger);
                }

                if (trigger.Type == TriggerType.Combo && Game.State.Combo == trigger.Combo)
                {
                    trigger.Triggerer = note;
                    OnTrigger(trigger);
                }

                if (trigger.Type == TriggerType.Score && Game.State.Score >= trigger.Score)
                {
                    trigger.Triggerer = note;
                    OnTrigger(trigger);
                    Triggers.Remove(trigger);
                }
            }
        }

        public void OnTrigger(Trigger trigger)
        {
            Renderer.OnTrigger(trigger);
            
            // Destroy trigger if needed
            trigger.CurrentUses++;
            if (trigger.CurrentUses == trigger.Uses)
            {
                Triggers.Remove(trigger);
            }
        }

        public JObject Compile()
        {
            var serializer = new JsonSerializer
            {
                NullValueHandling = NullValueHandling.Ignore
            };
            var root = new JObject();
            root["compiled"] = true;
            var texts = new JArray();
            Texts.Values.ForEach(it => texts.Add(JObject.FromObject(it, serializer)));
            root["texts"] = texts;
            var sprites = new JArray();
            Sprites.Values.ForEach(it => sprites.Add(JObject.FromObject(it, serializer)));
            root["sprites"] = sprites;
            var videos = new JArray();
            Videos.Values.ForEach(it => videos.Add(JObject.FromObject(it, serializer)));
            root["videos"] = videos;
            var lines = new JArray();
            Lines.Values.ForEach(it => lines.Add(JObject.FromObject(it, serializer)));
            root["lines"] = lines;
            var controllers = new JArray();
            Controllers.Values.ForEach(it => controllers.Add(JObject.FromObject(it, serializer)));
            root["controllers"] = controllers;
            var noteControllers = new JArray();
            NoteControllers.Values.ForEach(it => noteControllers.Add(JObject.FromObject(it, serializer)));
            root["note_controllers"] = noteControllers;
            return root;
        }

        private void RecursivelyParseTime(JObject obj)
        {
            foreach (var x in obj)
            {
                var name = x.Key;
                var value = x.Value;
                if (name == "time")
                {
                    value.Replace(ParseTime(obj, value));
                }
                else
                {
                    switch (value)
                    {
                        case JArray array:
                            RecursivelyParseTime(array);
                            break;
                        case JObject jObject:
                            RecursivelyParseTime(jObject);
                            break;
                    }
                }
            }
        }

        private void RecursivelyParseTime(JArray array)
        {
            foreach (var x in array)
            {
                switch (x)
                {
                    case JArray jArray:
                        RecursivelyParseTime(jArray);
                        break;
                    case JObject jObject:
                        RecursivelyParseTime(jObject);
                        break;
                }
            }
        }

        /**
         * Convert an object with an array of `time` to multiple objects.
         */
        private List<JObject> PopulateJObjects(JObject obj)
        {
            var timePopulatedObjects = new List<JObject>();

            var timeToken = obj.SelectToken("relative_time");
            if (timeToken != null && timeToken.Type == JTokenType.Array)
                foreach (var time in timeToken.Values())
                {
                    var newObj = (JObject) obj.DeepClone();
                    newObj["relative_time"] = time;
                    timePopulatedObjects.Add(newObj);
                }

            timeToken = obj.SelectToken("add_time");
            if (timeToken != null && timeToken.Type == JTokenType.Array)
                foreach (var time in timeToken.Values())
                {
                    var newObj = (JObject) obj.DeepClone();
                    newObj["add_time"] = time;
                    timePopulatedObjects.Add(newObj);
                }

            timeToken = obj.SelectToken("time");
            if (timeToken != null && timeToken.Type == JTokenType.Array)
                foreach (var time in timeToken.Values())
                {
                    var newObj = (JObject) obj.DeepClone();
                    newObj["time"] = time;
                    timePopulatedObjects.Add(newObj);
                }

            timePopulatedObjects = timePopulatedObjects.Count == 0 ? new List<JObject> {obj} : timePopulatedObjects;

            var populatedObjects = new List<JObject>();

            foreach (var obj2 in timePopulatedObjects)
            {
                var noteSpecifierToken = obj.SelectToken("note");
                if (noteSpecifierToken != null)
                {
                    if (noteSpecifierToken.Type == JTokenType.Object)
                    {
                        // Note selector
                        var noteSelector = new NoteSelector();
                        noteSelector.Start = (int?) noteSpecifierToken.SelectToken("start") ?? noteSelector.Start;
                        noteSelector.End = (int?) noteSpecifierToken.SelectToken("end") ?? noteSelector.End;
                        noteSelector.Direction = (int?) noteSpecifierToken.SelectToken("direction") ?? noteSelector.Direction;
                        noteSelector.MinX = (float?) noteSpecifierToken.SelectToken("min_x") ?? noteSelector.MinX;
                        noteSelector.MaxX = (float?) noteSpecifierToken.SelectToken("max_x") ?? noteSelector.MaxX;
                        var typeToken = noteSpecifierToken.SelectToken("type");
                        if (typeToken != null)
                        {
                            if (typeToken.Type == JTokenType.Integer)
                            {
                                noteSelector.Types.Add((int) typeToken);
                            }
                            else if (typeToken.Type == JTokenType.Array)
                            {
                                foreach (var noteToken in typeToken.Values())
                                {
                                    noteSelector.Types.Add((int) noteToken);
                                }
                            }
                        }
                        else
                        {
                            ((NoteType[]) Enum.GetValues(typeof(NoteType))).ForEach(it =>
                                noteSelector.Types.Add((int) it));
                        }

                        var noteIds = new List<int>();
                        foreach (var chartNote in Game.Chart.Model.note_list)
                        {
                            if (noteSelector.Types.Contains(chartNote.type)
                                && noteSelector.Start <= chartNote.id
                                && noteSelector.End >= chartNote.id
                                && noteSelector.MinX <= chartNote.x
                                && noteSelector.MaxX >= chartNote.x)
                            {
                                if (noteSelector.Direction == null || noteSelector.Direction == chartNote.direction)
                                {
                                    noteIds.Add(chartNote.id);
                                }
                            }
                        }

                        foreach (var noteId in noteIds)
                        {
                            var newObj = (JObject) obj2.DeepClone();
                            newObj["note"] = noteId;
                            populatedObjects.Add(newObj);
                        }
                    } 
                    else if (noteSpecifierToken.Type == JTokenType.Array)
                    {
                        foreach (var noteToken in noteSpecifierToken.Values())
                        {
                            var newObj = (JObject) obj2.DeepClone();
                            newObj["note"] = (int) noteToken;
                            populatedObjects.Add(newObj);
                        }
                    }
                    else if (noteSpecifierToken.Type == JTokenType.Integer)
                    {
                        var newObj = (JObject) obj2.DeepClone();
                        newObj["note"] = (int) noteSpecifierToken;
                        populatedObjects.Add(newObj);
                    }
                }
                else
                {
                    populatedObjects.Add(obj2);
                }
            }

            return populatedObjects;
        }

        private Trigger LoadTrigger(JToken token)
        {
            var json = token.ToObject<JObject>();
            var trigger = new Trigger();

            trigger.Type = json["type"] != null
                ? (TriggerType) Enum.Parse(typeof(TriggerType), (string) json["type"], true)
                : TriggerType.None;
            trigger.Uses = (int?) json.SelectToken("uses") ?? trigger.Uses;

            trigger.Notes = json["notes"] != null ? json.SelectToken("notes").Values<int>().ToList() : trigger.Notes;
            trigger.Spawn = json["spawn"] != null ? json.SelectToken("spawn").Values<string>().ToList() : trigger.Spawn;
            trigger.Destroy = json["destroy"] != null
                ? json.SelectToken("destroy").Values<string>().ToList()
                : trigger.Destroy;
            trigger.Combo = (int?) json.SelectToken("combo") ?? trigger.Combo;
            trigger.Score = (int?) json.SelectToken("score") ?? trigger.Score;

            return trigger;
        }

        private TO LoadObject<TO, TS>(JToken token) where TO : Object<TS>, new() where TS : ObjectState, new()
        {
            var states = new List<TS>();
            var obj = token.ToObject<JObject>();

            // Create initial state
            var initialState = (TS) CreateState((TS) null, obj);
            states.Add(initialState);
            // TODO: gfsd storyboard issue: template's first state should override initial state?

            // Create template states
            if (obj.TryGetValue("template", out var tmp))
            {
                var templateId = (string) tmp;
                var templateObject = Templates[templateId];

                // Template has states?
                if (templateObject["states"] != null)
                    AddStates(states, initialState, templateObject, ParseTime(obj, obj.SelectToken("time")));
            }

            // Create inline states
            AddStates(states, initialState, obj, ParseTime(obj, obj.SelectToken("time")));

            var id = (string) obj["id"] ?? Path.GetRandomFileName();
            var targetId = (string) obj.SelectToken("target_id");
            if (targetId != null && obj["id"] != null) throw new ArgumentException("Storyboard: A stage object cannot have both id and target_id");
            var parentId = (string) obj.SelectToken("parent_id");
            if (targetId != null && parentId != null) throw new ArgumentException("Storyboard: A stage object cannot have both target_id and parent_id");
            
            if (id.Contains("$note")) id = id.Replace("$note", ((int) replacements["note"]).ToString());
            if (targetId != null && targetId.Contains("$note")) targetId = targetId.Replace("$note", ((int) replacements["note"]).ToString());
            if (parentId != null && parentId.Contains("$note")) parentId = parentId.Replace("$note", ((int) replacements["note"]).ToString());

            return new TO
            {
                Id = id,
                TargetId = targetId,
                ParentId = parentId,
                States = states.OrderBy(state => state.Time).ToList() // Must sort by time
            };
        }

        private void AddStates<TS>(List<TS> states, TS baseState, JObject rootObject, float? rootBaseTime)
            where TS : ObjectState, new()
        {
            var baseTime = ParseTime(rootObject, rootObject.SelectToken("time")) ?? rootBaseTime ?? float.MaxValue; // We set this to float.MaxValue, so if time is not set, the object is not displayed

            if (rootObject["states"] != null && rootObject["states"].Type != JTokenType.Null)
            {
                var lastTime = baseTime;

                var allStates = new JArray();
                foreach (var childToken in (JArray) rootObject["states"])
                {
                    var populatedChildren = PopulateJObjects((JObject) childToken);
                    foreach (var child in populatedChildren) allStates.Add(child);
                }

                foreach (var stateJson in allStates)
                {
                    var stateObject = stateJson.ToObject<JObject>();
                    var objectState = CreateState(baseState, stateObject);

                    if (objectState.Time != float.MaxValue) baseTime = objectState.Time;

                    var relativeTime = (float?) stateObject["relative_time"];

                    if (relativeTime != null)
                    {
                        objectState.RelativeTime = (float) relativeTime;
                        // Use base time + relative time
                        objectState.Time = baseTime + (float) relativeTime;
                    }

                    var addTime = (float?) stateObject["add_time"];

                    if (addTime != null)
                    {
                        objectState.AddTime = (float) addTime;
                        // Use last time + add time
                        objectState.Time = lastTime + (float) addTime;
                    }

                    states.Add((TS) objectState);
                    baseState = (TS) objectState;

                    lastTime = objectState.Time;

                    // Add inline states
                    if (stateObject["states"] != null) AddStates(states, baseState, stateObject, rootBaseTime);
                }
            }
        }

        private ObjectState CreateState<TS>(TS baseState, JObject stateObject) where TS : ObjectState, new()
        {
            if ((bool?) stateObject["reset"] == true) baseState = null; // Allow resetting states

            // Load template
            JObject templateObject = null;
            if (stateObject["template"] != null)
            {
                var templateId = (string) stateObject["template"];
                templateObject = Templates[templateId];

                if (templateObject != null)
                {
                    // Put relative time and add time
                    if (stateObject["relative_time"] == null)
                        stateObject["relative_time"] = templateObject["relative_time"];

                    if (stateObject["add_time"] == null) stateObject["add_time"] = templateObject["add_time"];

                    // Put template states
                    if (stateObject["states"] == null) stateObject["states"] = templateObject["states"];
                }
            }

            var parser = CreateStateParser(typeof(TS));
            
            var state = baseState != null ? baseState.JsonDeepCopy() : new TS();
            if (templateObject != null) parser.Parse(state, templateObject, baseState);
            parser.Parse(state, stateObject, baseState);
            
            return state;
        }

        private readonly Dictionary<string, object> replacements = new Dictionary<string, object>();

        public float? ParseTime(JObject obj, JToken token)
        {
            if (token == null) return null;

            // var noteToken = obj.SelectToken("note");
            // if (noteToken != null)
            // {
            //     var value = (int) noteToken;
            //     replacements["note"] = value;
            //     return value;
            // }
            
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) return (float) token;

            if (token.Type == JTokenType.String)
            {
                var split = ((string) token).Split(':');
                var type = split[0].ToLower();
                var offset = 0f;
                if (split.Length == 3) offset = NumberUtils.ParseFloat(split[2]);

                var id = split[1].Let(it =>
                {
                    if (it == "$note")
                    {
                        var noteToken = obj.SelectToken("note");
                        if (noteToken != null)
                        {
                            var value = (int) noteToken;
                            replacements["note"] = value;
                            return value;
                        }
                        if (!replacements.ContainsKey("note"))
                        {
                            throw new Exception("$note not found");
                        }
                        return (int) replacements["note"];
                    }
                    return NumberUtils.ParseInt(it);
                });
                var note = Game.Chart.Model.note_map[id];
                switch (type)
                {
                    case "intro":
                        return note.intro_time + offset;
                    case "start":
                        return note.start_time + offset;
                    case "end":
                        return note.end_time + offset;
                    case "at":
                        return note.start_time + (note.end_time - note.start_time) * offset;
                }
            }

            return null;
        }
        
        private StateParser CreateStateParser(Type stateType)
        {
            if (stateType == typeof(TextState)) return new TextStateParser(this);
            if (stateType == typeof(SpriteState)) return new SpriteStateParser(this);
            if (stateType == typeof(LineState)) return new LineStateParser(this);
            if (stateType == typeof(VideoState)) return new VideoStateParser(this);
            if (stateType == typeof(NoteControllerState)) return new NoteControllerStateParser(this);
            if (stateType == typeof(ControllerState)) return new ControllerStateParser(this);
            throw new ArgumentException();
        }
        
    }
}
