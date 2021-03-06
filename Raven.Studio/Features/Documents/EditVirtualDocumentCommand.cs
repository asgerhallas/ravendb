﻿using System;
using Raven.Studio.Infrastructure;

namespace Raven.Studio.Features.Documents
{
    public class EditVirtualDocumentCommand : Command
    {
        private static readonly Func<string, int, DocumentNavigator> DefaultNavigatorFactory =
            (id, index) => DocumentNavigator.Create(id);

        public Func<string, int,DocumentNavigator> DocumentNavigatorFactory { get; set; }

        public EditVirtualDocumentCommand()
        {
        }

        public override bool CanExecute(object parameter)
        {
            var document = parameter as VirtualItem<ViewableDocument>;

            return document != null;
        }

        public override void Execute(object parameter)
        {
            var virtualItem = parameter as VirtualItem<ViewableDocument>;
            if (virtualItem == null || !virtualItem.IsRealized)
            {
                return;
            }

            var viewableDocument = virtualItem.Item;

            var navigatorFactory = DocumentNavigatorFactory ?? DefaultNavigatorFactory;

            var navigator = navigatorFactory(viewableDocument.Id, virtualItem.Index);

            UrlUtil.Navigate(navigator.GetUrl());
        }
    }
}