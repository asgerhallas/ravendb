﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Windows.Input;
using Microsoft.Expression.Interactivity.Core;
using Raven.Abstractions.Data;
using Raven.Client.Document;
using Raven.Studio.Commands;
using Raven.Studio.Features.Documents;
using Raven.Studio.Infrastructure;
using Raven.Client.Connection;
using Raven.Studio.Extensions;
using System.Reactive.Linq;
using Raven.Studio.Messages;
using Notification = Raven.Studio.Messages.Notification;

namespace Raven.Studio.Models
{
    public class DocumentsModel : ViewModel
    {
        private const string PriorityColumnsDocumentName = "Raven/Studio/PriorityColumns";
        private EditVirtualDocumentCommand editDocument;
        private Func<string, int, DocumentNavigator> documentNavigatorFactory;
        private IList<string> contextPriorityProperties; 
        public VirtualCollection<ViewableDocument> Documents { get; private set; }
        
        /// <summary>
        /// This property is used to give bound views a wrapper around the actual VirtualCollection to prevent memory leaks where
        /// a ListBox subscribes to the ICollectionView.CurrentChanged event, and doesn't unsubscribe
        /// </summary>
        public WeakCollectionViewWrapper<VirtualCollection<ViewableDocument>> DocumentsWeak { get { return new WeakCollectionViewWrapper<VirtualCollection<ViewableDocument>>(Documents); } }
 
        private ColumnsModel columns;

        private string header;
        private string context;

        private ICommand editColumns;
        private bool documentsHaveId;
        private ICommand deleteSelectedDocuments;
        private ICommand copyIdsToClipboard;
        private MostRecentUsedList<VirtualItem<ViewableDocument>> mostRecentDocuments = new MostRecentUsedList<VirtualItem<ViewableDocument>>(60);
        private ICommand copyDocumentTextToClipboard;
        private List<PriorityColumn> priorityColumns;
        private Func<DatabaseModel, IObservable<Unit>> observableGenerator;
        private IDisposable changesSubscription;
        private ICommand exportDetailsCommand;

        public event EventHandler<EventArgs> RecentDocumentsChanged;

        protected void OnRecentDocumentsChanged(EventArgs e)
        {
            EventHandler<EventArgs> handler = RecentDocumentsChanged;
            if (handler != null) handler(this, e);
        }

        public DocumentsModel(DocumentsVirtualCollectionSourceBase collectionSource)
        {
            Documents = new VirtualCollection<ViewableDocument>(collectionSource, 30, 30, new KeysComparer<ViewableDocument>(v => v.Id ?? v.DisplayId, v => v.LastModified, v => v.MetadataOnly));
            Documents.PropertyChanged += HandleDocumentsPropertyChanged;

            Observable.FromEventPattern<ItemsRealizedEventArgs>(h => Documents.ItemsRealized += h,
                                                                h => Documents.ItemsRealized -= h)
                .SampleResponsive(TimeSpan.FromSeconds(1))
                .ObserveOnDispatcher()
                .Subscribe(e => HandleItemsRealized(e.Sender, e.EventArgs));

            ItemSelection = new ItemSelection<VirtualItem<ViewableDocument>>();

            Context = "Default";
        }

        private void HandleDocumentsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Count" && Documents.Count == 0)
                mostRecentDocuments.Clear();
        }

        public ItemSelection<VirtualItem<ViewableDocument>> ItemSelection { get; private set; }

        public bool HideItemContextMenu { get; set; }

        public bool MinimalHeader { get; set; }

        private void HandleItemsRealized(object sender, ItemsRealizedEventArgs e)
        {
            var viewableDocument = Documents[e.StartingIndex].Item;
            
            // collection may have been reset (and hence the item cleared) since the event was raised, thus the null check
            if (viewableDocument != null)
                DocumentsHaveId = !string.IsNullOrEmpty(viewableDocument.Id);

            // When a view is refreshed, items can be realized in different orders (depending on the order the query responses come back from the db)
            // So to stabilise the column set, we keep a list of 60 most recently used documents, and then sort them in index order. 
            mostRecentDocuments.AddRange(Enumerable.Range(e.StartingIndex, e.Count).Select(i => Documents[i]));

            if (Columns.Source == ColumnsSource.Automatic)
            {
                var newColumns = GetCurrentColumnsSuggestion();

                if (!Columns.Columns.Select(c => c.Binding).SequenceEqual(newColumns.Select(c => c.Binding)))
                    Columns.LoadFromColumnDefinitions(newColumns);
            }

            OnRecentDocumentsChanged(EventArgs.Empty);
        }

        private IList<ColumnDefinition> GetCurrentColumnsSuggestion()
        {
            var suggester = new ColumnSuggester();

            var priorityColumns = this.priorityColumns;

            if (contextPriorityProperties != null && contextPriorityProperties.Count > 0)
            {
                priorityColumns = contextPriorityProperties
                    .Select(p => new PriorityColumn() { PropertyNamePattern = "^" + p.Replace(".", "\\.") + "$"})
                    .Concat(priorityColumns.EmptyIfNull())
                    .ToList();
            }

            var newColumns = suggester.AutoSuggest(GetMostRecentDocuments(), Context, priorityColumns);

            return newColumns;
        }

        public IEnumerable<ViewableDocument> GetMostRecentDocuments()
        {
            return mostRecentDocuments
                .Where(i => i.IsRealized)
                .OrderBy(i => i.Index)
                .Select(i => i.Item);
        }

        public string Context
        {
            get { return context; }
            set
            {
                context = value ?? "Default";
                UpdateColumnSet();
            }
        }

        public bool DocumentsHaveId
        {
            get { return documentsHaveId; }
            set
            {
                if (documentsHaveId != value)
                {
                    documentsHaveId = value;
                    OnPropertyChanged(() => DocumentsHaveId);
                }
            }
        }

        private void UpdateColumnSet()
        {
            if (!IsLoaded || ApplicationModel.Database.Value == null)
            {
                return;
            }

            var columnsModel = PerDatabaseState.DocumentViewState.GetDocumentState(context);

            if (columnsModel != null)
            {
                Columns = columnsModel;
            }
            else
            {
                Columns = new ColumnsModel();
                PerDatabaseState.DocumentViewState.SetDocumentState(context, Columns);

                TryLoadDefaultColumnSet();
            }
        }

        public ColumnsModel Columns
        {
            get { return columns; }
            private set
            {
                columns = value;
                OnPropertyChanged(() => Columns);
            }
        }

        public Func<string, int, DocumentNavigator> DocumentNavigatorFactory
        {
            get { return documentNavigatorFactory; }
            set
            {
                documentNavigatorFactory = value;
                if (editDocument != null)
                    editDocument.DocumentNavigatorFactory = value;
            }
        }

        public ICommand EditDocument { get
        {
            return editDocument ??
                   (editDocument =
                    new EditVirtualDocumentCommand() {DocumentNavigatorFactory = DocumentNavigatorFactory});
        } }

        public ICommand EditColumns
        {
            get { return editColumns ?? (editColumns = new ActionCommand(HandleEditColumns)); }
        }

        public ICommand DeleteSelectedDocuments
        {
            get { return deleteSelectedDocuments ?? (deleteSelectedDocuments = new DeleteDocumentsCommand(ItemSelection)); }
        }

        public ICommand CopyIdsToClipboard
        {
            get { return copyIdsToClipboard ?? (copyIdsToClipboard = new CopyDocumentsIdsCommand(ItemSelection)); }
        }

        public ICommand CopyDocumentTextToClipboard
        {
            get
            {
                return copyDocumentTextToClipboard ??
                       (copyDocumentTextToClipboard = new CopyDocumentsToClipboardCommand(ItemSelection));
            }
        }

        public ICommand ExportDetails
        {
            get { return exportDetailsCommand ?? (exportDetailsCommand = new ExportDocumentDetailsCommand(this)); }
        }

        public bool IsExportEnabled { get { return DocumentSize.Current.DisplayStyle == DocumentDisplayStyle.Details; }}

        protected override void OnViewLoaded()
        {
            UpdateColumnSet();

            BeginLoadPriorityProperties();

            DocumentSize.Current.ObservePropertyChanged()
                .TakeUntil(Unloaded)
                .Where(c => c.EventArgs.PropertyName == "DisplayStyle")
                .Subscribe(_ => HandleDisplayStyleChanged());

            ApplicationModel.Database
                .ObservePropertyChanged()
                .TakeUntil(Unloaded)
                .Subscribe(_ =>
                               {
                                   BeginLoadPriorityProperties();
                                   UpdateColumnSet();
                                   ObserveSourceChanges();
                                   Documents.Refresh(RefreshMode.ClearStaleData);
                               });

            ObserveSourceChanges();

            Documents.Refresh(RefreshMode.ClearStaleData);
            HandleDisplayStyleChanged();
        }

        private void HandleDisplayStyleChanged()
        {
            (Documents.Source as DocumentsVirtualCollectionSourceBase).MetadataOnly =
                DocumentSize.Current.DisplayStyle == DocumentDisplayStyle.IdOnly;

            OnPropertyChanged(() => IsExportEnabled);
        }

        protected override void OnViewUnloaded()
        {
            base.OnViewUnloaded();

            StopListeningForChanges();
        }

        public void SetChangesObservable(Func<DatabaseModel, IObservable<Unit>> observableGenerator)
        {
            this.observableGenerator = observableGenerator;
            ObserveSourceChanges();
        }

        private void ObserveSourceChanges()
        {
            if (!IsLoaded)
                return;

            StopListeningForChanges();

            var databaseModel = ApplicationModel.Database.Value;

            if (observableGenerator != null && databaseModel != null)
            {
                var observable = observableGenerator(databaseModel);
                changesSubscription = 
                    observable
                    .SampleResponsive(TimeSpan.FromSeconds(1))
                    .ObserveOnDispatcher()
                    .Subscribe(_ => Documents.Refresh(RefreshMode.PermitStaleDataWhilstRefreshing));
            }
        }

        private void StopListeningForChanges()
        {
            if (changesSubscription != null)
                changesSubscription.Dispose();
        }

        public void SetPriorityColumns(IList<string> priorityColumns)
        {
            contextPriorityProperties = priorityColumns;
            UpdateColumnSet();
        }

        private void BeginLoadPriorityProperties()
        {
            if (ApplicationModel.Database.Value == null)
                return;

            ApplicationModel.Database.Value
                .AsyncDatabaseCommands
                .GetAsync(PriorityColumnsDocumentName)
                .ContinueOnSuccessInTheUIThread(CompleteLoadPriorityProperties);
        }

        private void CompleteLoadPriorityProperties(JsonDocument document)
        {
            if (document != null)
            {
                var unvalidatedPriorityColumns = document.DataAsJson.Deserialize<ListContainer<PriorityColumn>>(new DocumentConvention()).Items.EmptyIfNull();

                var validatedPriorityColumns = new List<PriorityColumn>();

                foreach (var priorityColumn in unvalidatedPriorityColumns)
                {
                    if (!priorityColumn.PropertyNamePattern.IsValidRegex())
                    {
                        ApplicationModel.Current.AddNotification(
                            new Notification(string.Format("Pattern '{0}' in '{1}' is not a valid regular expression", priorityColumn.PropertyNamePattern, PriorityColumnsDocumentName), NotificationLevel.Error));
                    }
                    else
                    {
                        validatedPriorityColumns.Add(priorityColumn);
                    }
                }

                priorityColumns = validatedPriorityColumns;
            }
            else
            {
                priorityColumns = null;
            }
        }

        private void TryLoadDefaultColumnSet()
        {
            var contextWhenRequested = Context;

            ApplicationModel.DatabaseCommands
                .GetAsync("Raven/Studio/Columns/" + Context)
                .ContinueOnSuccessInTheUIThread(result => UpdateColumns(result, contextWhenRequested));
        }

        private void UpdateColumns(JsonDocument columnSetDocument, string contextWhenRequested)
        {
            if (contextWhenRequested != Context)
                return;

            if (columnSetDocument != null)
            {
                var columnSet = columnSetDocument.DataAsJson.Deserialize<ColumnSet>(new DocumentConvention() {});
                Columns.LoadFromColumnDefinitions(columnSet.Columns);
                Columns.Source = ColumnsSource.User;
            }
        }

        public string Header
        {
            get { return header ?? (header = "Documents"); }
            set
            {
                header = value;
                OnPropertyChanged(() => Header);
            }
        }

        private void HandleEditColumns()
        {
            ColumnsEditorDialog.Show(
                Columns, 
                Context, 
                () => new ColumnSuggester().AllSuggestions(GetMostRecentDocuments()),
                GetCurrentColumnsSuggestion);
        }
    }
}