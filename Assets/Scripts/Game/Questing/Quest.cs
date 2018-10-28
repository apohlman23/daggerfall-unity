// Project:         Daggerfall Tools For Unity
// Copyright:       Copyright (C) 2009-2018 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using DaggerfallWorkshop.Utility;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using FullSerializer;

namespace DaggerfallWorkshop.Game.Questing
{
    /// <summary>
    /// Contains live state of quests in play.
    /// Quests are instantiated from text source and executed inside quest machine.
    /// Each quest is assigned a unique id (UID) as its possible for same question to be instantiated multiple times,
    /// such as a basic fetch quest to two different dungeons. The name of quest cannot not be used for unique identification.
    /// Child resources generally will not care about quest UID, but this is used by quest machine.
    /// </summary>
    public partial class Quest : IDisposable
    {
        #region Fields

        public const int QuestSuccessRep = 5;
        public const int QuestFailureRep = -2;

        // Quest object collections
        Dictionary<int, Message> messages = new Dictionary<int, Message>();
        Dictionary<string, Task> tasks = new Dictionary<string, Task>();

        // Clock, Place, Person, Foe, etc. all share a common resource dictionary
        Dictionary<string, QuestResource> resources = new Dictionary<string, QuestResource>();

        // Questors as added by quest script
        Dictionary<string, QuestorData> questors = new Dictionary<string, QuestorData>();

        ulong uid;
        bool questComplete = false;
        bool questSuccess = false;
        Dictionary<int, LogEntry> activeLogMessages = new Dictionary<int, LogEntry>();

        string questName;
        string displayName;
        DaggerfallDateTime questStartTime;
        int factionId = 0;
        IMacroContextProvider mcp = null;

        bool questTombstoned = false;
        DaggerfallDateTime questTombstoneTime;

        Place lastPlaceReferenced = null;
        QuestResource lastResourceReferenced = null;
        bool questBreak = false;

        int ticksToEnd = 0;

        #endregion

        #region Structures

        /// <summary>
        /// Stores active log messages as ID only.
        /// This allows text to be linked/unlinked into log without duplication of text.
        /// Actual quest UI can decide how to present these messages to user.
        /// </summary>
        [Serializable]
        public struct LogEntry
        {
            public int stepID;
            public int messageID;
        }

        /// <summary>
        /// Basic information about a single task
        /// </summary>
        [Serializable]
        public struct TaskState
        {
            public Task.TaskType type;
            public Symbol symbol;
            public bool set;
        }

        [Serializable]
        public struct QuestorData
        {
            public Symbol symbol;
            public string name;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets quest UID assigned at create time.
        /// </summary>
        public ulong UID
        {
            get { return uid; }
        }

        /// <summary>
        /// True when quest has completed and will be tombstoned by quest machine.
        /// </summary>
        public bool QuestComplete
        {
            get { return questComplete; }
        }

        /// <summary>
        /// True when quest executes a "give pc" action, which indicates quest success.
        /// If quest ends without this being set, quest can be considered failed.
        /// </summary>
        public bool QuestSuccess
        {
            get { return questSuccess; }
            set { questSuccess = value; }
        }

        /// <summary>
        /// Short quest name read from source.
        /// e.g. "_BRISIEN"
        /// </summary>
        public string QuestName
        {
            get { return questName; }
            set { questName = value; }
        }

        /// <summary>
        /// Optional display name for future quest journal.
        /// e.g. "Lady Brisienna's Letter"
        /// </summary>
        public string DisplayName
        {
            get { return displayName; }
            set { displayName = value; }
        }

        /// <summary>
        /// Faction ID the quest will affect reputation of.
        /// </summary>
        public int FactionId
        {
            get { return factionId; }
            set { factionId = value; }
        }

        /// <summary>
        /// External non-quest context provider.
        /// </summary>
        public IMacroContextProvider ExternalMCP
        {
            get { return mcp; }
            set { mcp = value; }
        }

        /// <summary>
        /// Gets world time of quest when started.
        /// </summary>
        public DaggerfallDateTime QuestStartTime
        {
            get { return questStartTime; }
        }

        /// <summary>
        /// Gets or sets last Place resource encountered during macro expand.
        /// This will be used to resolve di, etc.
        /// Can return null so caller should have a fail-over plan.
        /// </summary>
        public Place LastPlaceReferenced
        {
            get { return lastPlaceReferenced; }
            set { lastPlaceReferenced = value; }
        }

        /// <summary>
        /// Gets or sets last QuestResource for Person/Foe encountered during macro expand.
        /// This will be used to resolve pronoun, god, oath, etc.
        /// Can return null so caller should have a fail-over plan.
        /// </summary>
        public QuestResource LastResourceReferenced
        {
            get { return lastResourceReferenced; }
            set { lastResourceReferenced = value; }
        }

        /// <summary>
        /// Allows other classes working on this quest to break execution.
        /// This allows for "say" messages and other popups to happen in correct order.
        /// Flag will be lowered automatically.
        /// </summary>
        public bool QuestBreak
        {
            get { return questBreak; }
            set { questBreak = value; }
        }

        /// <summary>
        /// True when quest has been tombstoned by QuestMachine.
        /// Quest will persist a while after completion for post-quest rumours, failure text, etc.
        /// </summary>
        public bool QuestTombstoned
        {
            get { return questTombstoned; }
        }

        /// <summary>
        /// The time quest was tombstoned.
        /// Not valid if quest has not yet been tombstoned.
        /// </summary>
        public DaggerfallDateTime QuestTombstoneTime
        {
            get { return questTombstoneTime; }
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Default constructor.
        /// </summary>
        public Quest()
        {
            uid = DaggerfallUnity.NextUID;
            questStartTime = new DaggerfallDateTime(DaggerfallUnity.Instance.WorldTime.Now);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Update quest.
        /// </summary>
        public void Update()
        {
            // Do nothing if complete
            // Now waiting to be tombstoned in quest machine
            if (questComplete)
            {
                // Do nothing further if complete
                // Now waiting to be tombstoned in quest machine
                return;
            }
            // Countdown ticks to end
            if (ticksToEnd > 0)
            {
                if (--ticksToEnd == 0)
                    questComplete = true;
            }

            // Tick resources
            foreach(QuestResource resource in resources.Values)
            {
                resource.Tick(this);
            }

            // Update tasks
            foreach(Task task in tasks.Values)
            {
                // Hard ignore dropped tasks
                if (task.IsDropped)
                    continue;

                // Handle quest break or completion
                if (questBreak || questComplete)
                {
                    questBreak = false;
                    return;
                }

                task.Update();
            }

            // PostTick resources
            foreach (QuestResource resource in resources.Values)
            {
                resource.PostTick(this);
            }
        }

        /// <summary>
        /// initializes quest rumors
        /// dictionary must contain the quest's messages (call after messages was initialized correctly)
        /// </summary>
        public void initQuestRumors()
        {
            // Add RumorsDuringQuest rumor to rumor mill
            Message message;
            message = GetMessage((int)QuestMachine.QuestMessages.RumorsDuringQuest);
            if (message != null)
            {
                GameManager.Instance.TalkManager.AddOrReplaceQuestProgressRumor(this.UID, message);
            }
        }

        public void EndQuest()
        {
            // Schedule quest to end after a couple of ticks
            // This allows any final tasks started directly before "end quest" to execute
            // Example is Sx017 when Akorithi prompts if PC used painting then ends quest
            // There might be a better way to handle this (e.g. prompt executes task directly rather than on next tick)
            ticksToEnd = 2;

            // remove quest progress rumors for this quest from talk manager
            GameManager.Instance.TalkManager.RemoveQuestProgressRumorsFromRumorMill(this.UID);

            // remove all quest's questor messages about quest success/failure
            GameManager.Instance.TalkManager.RemoveQuestorPostQuestMessage(this.UID);

            // Update faction reputation if this quest was for a specific faction
            if (factionId > 0)
            {
                int repChange = QuestSuccess ? QuestSuccessRep : QuestFailureRep;
                GameManager.Instance.PlayerEntity.FactionData.ChangeReputation(factionId, repChange, true);
            }
            // Add the active quest messages to player notebook.
            LogEntry[] logEntries = GetLogMessages();
            if (logEntries != null && logEntries.Length > 0)
            {
                foreach (var logEntry in logEntries)
                {
                    var message = GetMessage(logEntry.messageID);
                    if (message != null)
                        GameManager.Instance.PlayerEntity.Notebook.AddFinishedQuest(message.GetTextTokens());
                }
            }
        }

        public void StartTask(Symbol symbol)
        {
            Task task = GetTask(symbol);
            if (task != null)
                task.Start();
        }

        public void ClearTask(Symbol symbol)
        {
            Task task = GetTask(symbol);
            if (task != null)
                task.Clear();
        }

        public TaskState[] GetTaskStates()
        {
            List<TaskState> states = new List<TaskState>();
            foreach(Task task in tasks.Values)
            {
                TaskState state = new TaskState();
                state.type = task.Type;
                state.symbol = task.Symbol;
                state.set = task.IsTriggered;
                states.Add(state);
            }

            return states.ToArray();
        }

        public void Dispose()
        {
            // Dispose of quest resources
            foreach(QuestResource resource in resources.Values)
            {
                resource.Dispose();
            }
        }

        /// <summary>
        /// Start tracking a new questor.
        /// </summary>
        /// <param name="personSymbol">Symbol of new questor.</param>
        public void AddQuestor(Symbol personSymbol)
        {
            // Must be a valid resource
            if (personSymbol == null || string.IsNullOrEmpty(personSymbol.Name))
                throw new Exception("AddQuestor() must receive a named symbol.");

            // Person must not be a questor already
            if (questors.ContainsKey(personSymbol.Name))
            {
                Debug.LogWarningFormat("Person {0} is already a questor for quest {1} [{2}]", personSymbol.Original, uid, displayName);
                return;
            }

            // Attempt to get person resources
            Person person = GetPerson(personSymbol);
            if (person == null)
            {
                Debug.LogWarningFormat("Could not find matching Person resource to add questor {0}", personSymbol.Original);
                return;
            }

            // Create new questor
            string key = personSymbol.Name;
            QuestorData qd = new QuestorData();
            qd.symbol = personSymbol.Clone();
            qd.name = person.DisplayName;
            questors.Add(personSymbol.Name, qd);

            // Dynamically relink individual NPC and associated QuestResourceBehaviour (if any) in current scene
            if (person.IsIndividualNPC)
            {
                QuestResourceBehaviour[] behaviours = GameObject.FindObjectsOfType<QuestResourceBehaviour>();
                foreach (var questResourceBehaviour in behaviours)
                {
                    // Get StaticNPC if present
                    StaticNPC npc = questResourceBehaviour.GetComponent<StaticNPC>();
                    if (!npc)
                        continue;

                    // Link up resource and behaviour if this person found in scene
                    if (person.FactionData.id == npc.Data.factionID)
                    {
                        questResourceBehaviour.AssignResource(person);
                        person.QuestResourceBehaviour = questResourceBehaviour;
                    }
                }
            }
        }

        /// <summary>
        /// Stop tracking an existing questor.
        /// </summary>
        /// <param name="personSymbol">Symbol of questor to drop.</param>
        public void DropQuestor(Symbol personSymbol)
        {
            // Must be a valid resource
            if (personSymbol == null || string.IsNullOrEmpty(personSymbol.Name))
                throw new Exception("DropQuestor() must receive a named symbol.");

            // Remove questor if present
            string key = personSymbol.Name;
            if (questors.ContainsKey(key))
            {
                questors.Remove(key);
            }
        }

        #endregion

        #region Log Message Methods

        /// <summary>
        /// Adds quest log message for quest at step position.
        /// Quests can only have 0-9 steps and cannot log the same step more than once at a time.
        /// </summary>
        /// <param name="stepID">StepID to key this message.</param>
        /// <param name="messageID">MessageID to display for this step.</param>
        public void AddLogStep(int stepID, int messageID)
        {
            // Replacing existing log step if it exists
            if (activeLogMessages.ContainsKey(stepID))
            {
                activeLogMessages.Remove(stepID);
            }

            // Add the step to active log messages
            LogEntry entry = new LogEntry();
            entry.stepID = stepID;
            entry.messageID = messageID;
            activeLogMessages.Add(stepID, entry);
        }

        /// <summary>
        /// Removes quest log message for step position.
        /// </summary>
        /// <param name="stepID">StepID to remove from quest log.</param>
        public void RemoveLogStep(int stepID)
        {
            // Remove the step
            if (activeLogMessages.ContainsKey(stepID))
            {
                activeLogMessages.Remove(stepID);
            }
        }

        /// <summary>
        /// Gets the active log messages associated with this quest.
        /// Usually only one log step is active at a time.
        /// This allows log messages to be displayed however desired (as list, verbose, sorted, etc.).
        /// </summary>
        /// <returns>LogEntry array, or null if quest completed.</returns>
        public LogEntry[] GetLogMessages()
        {
            // Return null if quest is finished
            if (QuestComplete)
                return null;

            // Create an array of active log messages
            LogEntry[] logs = new LogEntry[activeLogMessages.Count];
            int count = 0;
            foreach (LogEntry log in activeLogMessages.Values)
            {
                logs[count] = log;
                count++;
            }

            return logs;
        }

        /// <summary>
        /// Marks quest as tombstoned and schedules for eventual deletion.
        /// </summary>
        public void TombstoneQuest()
        {
            questTombstoned = true;
            questComplete = true;
            questTombstoneTime = new DaggerfallDateTime(DaggerfallUnity.Instance.WorldTime.Now);

            Message messageRumors;
            Message messageQuestor;
            // add RumorsPostSuccess, RumorsPostFailure, QuestorPostSuccess or QuestorPostFailure rumor to rumor mill                
            if (questSuccess)
            {
                messageRumors = GetMessage((int)QuestMachine.QuestMessages.RumorsPostSuccess);
                messageQuestor = GetMessage((int)QuestMachine.QuestMessages.QuestorPostSuccess);
            }
            else
            {
                messageRumors = GetMessage((int)QuestMachine.QuestMessages.RumorsPostFailure);
                messageQuestor = GetMessage((int)QuestMachine.QuestMessages.QuestorPostFailure);
            }
            if (messageRumors != null)
                GameManager.Instance.TalkManager.AddOrReplaceQuestProgressRumor(this.UID, messageRumors);
            if (messageQuestor != null)
                GameManager.Instance.TalkManager.AddQuestorPostQuestMessage(this.UID, messageQuestor);

            // remove quest rumors (rumor mill command) for this quest from talk manager
            GameManager.Instance.TalkManager.RemoveQuestRumorsFromRumorMill(this.UID);

            // remove all quest topics for this quest from talk manager
            GameManager.Instance.TalkManager.RemoveQuestInfoTopicsForSpecificQuest(this.UID);
        }

        #endregion

        #region Resource Query Methods

        public Message GetMessage(int messageID)
        {
            if (messages.ContainsKey(messageID))
                return messages[messageID];
            else
                return null;
        }

        public Task GetTask(Symbol symbol)
        {
            if (symbol != null && tasks.ContainsKey(symbol.Name))
                return tasks[symbol.Name];
            else
                return null;
        }

        public Clock GetClock(Symbol symbol)
        {
            return GetResource(symbol) as Clock;
        }

        public Place GetPlace(Symbol symbol)
        {
            return GetResource(symbol) as Place;
        }

        public Person GetPerson(Symbol symbol)
        {
            return GetResource(symbol) as Person;
        }

        public Item GetItem(Symbol symbol)
        {
            return GetResource(symbol) as Item;
        }

        public Foe GetFoe(Symbol symbol)
        {
            return GetResource(symbol) as Foe;
        }

        public QuestResource GetResource(string name)
        {
            if (!string.IsNullOrEmpty(name) && resources.ContainsKey(name))
                return resources[name];
            else
                return null;
        }

        public QuestResource GetResource(Symbol symbol)
        {
            if (symbol != null && resources.ContainsKey(symbol.Name))
                return resources[symbol.Name];
            else
                return null;
        }

        public QuestResource[] GetAllResources(Type resourceType)
        {
            List<QuestResource> foundResources = new List<QuestResource>();

            foreach (var kvp in resources)
            {
                if (kvp.Value.GetType() == resourceType)
                {
                    foundResources.Add(kvp.Value);
                }
            }

            return foundResources.ToArray();
        }

        public Symbol[] GetQuestors()
        {
            List<Symbol> foundQuestors = new List<Symbol>();
            foreach (var kvp in questors)
            {
                foundQuestors.Add(kvp.Value.symbol);
            }

            return foundQuestors.ToArray();
        }

        public DaggerfallMessageBox ShowMessagePopup(int id)
        {
            const int chunkSize = 22;

            // Get message resource
            Message message = GetMessage(id);
            if (message == null)
                return null;

            // Get all message tokens
            TextFile.Token[] tokens = message.GetTextTokens();
            if (tokens == null || tokens.Length == 0)
                return null;

            // Split token lines into chunks for display
            // This break huge blocks of text into multiple popups
            int lineCount = 0;
            List<TextFile.Token> currentChunk = new List<TextFile.Token>();
            List<TextFile.Token[]> chunks = new List<TextFile.Token[]>();
            for (int i = 0; i < tokens.Length; i++)
            {
                // Add current token
                currentChunk.Add(tokens[i]);

                // Count new lines and start a new chunk at max size
                if (tokens[i].formatting == TextFile.Formatting.JustifyCenter ||
                    tokens[i].formatting == TextFile.Formatting.JustifyLeft ||
                    tokens[i].formatting == TextFile.Formatting.Nothing)
                {
                    if (++lineCount > chunkSize)
                    {
                        chunks.Add(currentChunk.ToArray());
                        currentChunk.Clear();
                        lineCount = 0;
                    }
                }
            }

            // Add final chunk only if not empty
            if (currentChunk.Count > 0)
                chunks.Add(currentChunk.ToArray());

            // Display message boxes in reverse order - this is the previous way of showing stacked popups
            DaggerfallMessageBox rootMessageBox = null;
            for (int i = chunks.Count - 1; i >= 0; i--)
            {
                DaggerfallMessageBox messageBox = new DaggerfallMessageBox(DaggerfallUI.UIManager);
                messageBox.SetTextTokens(chunks[i]);
                messageBox.ClickAnywhereToClose = true;
                messageBox.AllowCancel = true;
                messageBox.ParentPanel.BackgroundColor = Color.clear;
                messageBox.Show();

                if (i == 0)
                    rootMessageBox = messageBox;
            }

            //// Compose root message box and use AddNextMessageBox() - this is a new technique added by Hazelnut
            //// TODO: Currently when linking more than two message boxes the final two boxes loop between each other
            //// Will return to this later to check, for now just want to continue with quest work
            //DaggerfallMessageBox rootMessageBox = new DaggerfallMessageBox(DaggerfallUI.UIManager);
            //rootMessageBox.SetTextTokens(chunks[0]);
            //rootMessageBox.ClickAnywhereToClose = true;
            //rootMessageBox.AllowCancel = true;
            //rootMessageBox.ParentPanel.BackgroundColor = Color.clear;

            //// String together remaining message boxes (if any)
            //DaggerfallMessageBox lastMessageBox = rootMessageBox;
            //for (int i = 1; i < chunks.Count; i++)
            //{
            //    DaggerfallMessageBox thisMessageBox = new DaggerfallMessageBox(DaggerfallUI.UIManager);
            //    thisMessageBox.SetTextTokens(chunks[i]);
            //    thisMessageBox.ClickAnywhereToClose = true;
            //    thisMessageBox.AllowCancel = true;
            //    thisMessageBox.ParentPanel.BackgroundColor = Color.clear;
            //    lastMessageBox.AddNextMessageBox(thisMessageBox);
            //    lastMessageBox = thisMessageBox;
            //}
            //rootMessageBox.Show();

            // Set a quest break so popup will display immediately
            questBreak = true;

            return rootMessageBox;
        }

        #endregion

        #region Resource Allocation Methods

        public void AddMessage(int messageID, Message message)
        {
            messages.Add(messageID, message);
        }

        public void AddResource(QuestResource resource)
        {
            if (resource.Symbol == null || string.IsNullOrEmpty(resource.Symbol.Name))
            {
                throw new Exception("QuestResource must have a named symbol.");
            }

            if (resources.ContainsKey(resource.Symbol.Name))
            {
                throw new Exception(string.Format("Duplicate QuestResource symbol name found: {0}", resource.Symbol));
            }

            resources.Add(resource.Symbol.Name, resource);
        }

        public void AddTask(Task task)
        {
            if (!tasks.ContainsKey(task.Symbol.Name))
                tasks.Add(task.Symbol.Name, task);
            else
            {
                //task w/ this symbol already exists, add actions to it
                var existingTask = tasks[task.Symbol.Name];
                existingTask.CopyQuestActions(task);
            }
        }

        #endregion

        #region Serialization

        [fsObject("v1")]
        public struct QuestSaveData_v1
        {
            public ulong uid;
            public bool questComplete;
            public bool questSuccess;
            public string questName;
            public string displayName;
            public int factionId;
            public DaggerfallDateTime questStartTime;
            public bool questTombstoned;
            public DaggerfallDateTime questTombstoneTime;
            public LogEntry[] activeLogMessages;
            public Message.MessageSaveData_v1[] messages;
            public QuestResource.ResourceSaveData_v1[] resources;
            public Dictionary<string, QuestorData> questors;
            public Task.TaskSaveData_v1[] tasks;
        }

        public QuestSaveData_v1 GetSaveData()
        {
            // Save base state
            QuestSaveData_v1 data = new QuestSaveData_v1();
            data.uid = uid;
            data.questComplete = questComplete;
            data.questSuccess = questSuccess;
            data.questName = questName;
            data.displayName = displayName;
            data.factionId = factionId;
            data.questStartTime = questStartTime;
            data.questTombstoned = questTombstoned;
            data.questTombstoneTime = questTombstoneTime;

            // Save active log messages
            List<LogEntry> activeLogMessagesSaveDataList = new List<LogEntry>();
            foreach(LogEntry logEntry in activeLogMessages.Values)
            {
                activeLogMessagesSaveDataList.Add(logEntry);
            }
            data.activeLogMessages = activeLogMessagesSaveDataList.ToArray();

            // Save messages
            List<Message.MessageSaveData_v1> messageSaveDataList = new List<Message.MessageSaveData_v1>();
            foreach(Message message in messages.Values)
            {
                messageSaveDataList.Add(message.GetSaveData());
            }
            data.messages = messageSaveDataList.ToArray();

            // Save resources
            List<QuestResource.ResourceSaveData_v1> resourceSaveDataList = new List<QuestResource.ResourceSaveData_v1>();
            foreach(QuestResource resource in resources.Values)
            {
                resourceSaveDataList.Add(resource.GetResourceSaveData());
            }
            data.resources = resourceSaveDataList.ToArray();

            // Save questors
            data.questors = questors;

            // Save tasks
            List<Task.TaskSaveData_v1> taskSaveDataList = new List<Task.TaskSaveData_v1>();
            foreach(Task task in tasks.Values)
            {
                Task.TaskSaveData_v1 taskData = task.GetSaveData();
                taskSaveDataList.Add(taskData);
            }
            data.tasks = taskSaveDataList.ToArray();

            return data;
        }

        public void RestoreSaveData(QuestSaveData_v1 data)
        {
            // Restore base state
            uid = data.uid;
            questComplete = data.questComplete;
            questSuccess = data.questSuccess;
            questName = data.questName;
            displayName = data.displayName;
            factionId = data.factionId;
            questStartTime = data.questStartTime;
            questTombstoned = data.questTombstoned;
            questTombstoneTime = data.questTombstoneTime;

            // Restore active log messages
            activeLogMessages.Clear();
            foreach(LogEntry logEntry in data.activeLogMessages)
            {
                activeLogMessages.Add(logEntry.stepID, logEntry);
            }

            // Restore messages
            messages.Clear();
            foreach(Message.MessageSaveData_v1 messageData in data.messages)
            {
                Message message = new Message(this);
                message.RestoreSaveData(messageData);
                messages.Add(message.ID, message);
            }

            // Restore resources
            resources.Clear();
            foreach(QuestResource.ResourceSaveData_v1 resourceData in data.resources)
            {
                // Construct deserialized QuestResource based on type
                System.Reflection.ConstructorInfo ctor = resourceData.type.GetConstructor(new Type[] { typeof(Quest) });
                QuestResource resource = (QuestResource)ctor.Invoke(new object[] { this });

                // Restore state
                resource.RestoreResourceSaveData(resourceData);
                resources.Add(resource.Symbol.Name, resource);
            }

            // Restore questors
            questors.Clear();
            if (data.questors != null)
            {
                questors = data.questors;
            }

            // Restore tasks
            tasks.Clear();
            foreach(Task.TaskSaveData_v1 taskData in data.tasks)
            {
                Task task = new Task(this);
                task.RestoreSaveData(taskData);
                tasks.Add(task.Symbol.Name, task);
            }
        }

        #endregion
    }
}