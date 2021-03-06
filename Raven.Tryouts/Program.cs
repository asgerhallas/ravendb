﻿using System;
using System.Collections.Generic;
using System.Threading;
using Raven.Abstractions;
using Raven.Abstractions.Commands;
using Raven.Abstractions.Data;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Linq;
using Raven.Abstractions.Logging;
using Raven.Abstractions.MEF;
using Raven.Client.Document;
using Raven.Database;
using Raven.Database.Config;
using Raven.Database.Impl;
using Raven.Database.Plugins;
using Raven.Database.Storage;
using Raven.Json.Linq;
using Raven.Tests.Bugs;
using Raven.Tests.Document;
using Raven.Tests.Issues;
using System.Linq;
using Raven.Tests.Util;

namespace Raven.Tryouts
{
	internal class Program
	{
		[STAThread]
		private static void Main()
		{
			{
				var x = new DocumentStoreServerTests_DifferentProcess();
				x.Can_promote_transactions();
			}

			using(var x = new RunExternalProcess())
			{
				x.can_use_RavenDB_in_a_remote_process();
			}

			using (var x = new RunExternalProcess())
			{
				x.can_use_RavenDB_in_a_remote_process_for_batch_operations();
			}

			using (var x = new RunExternalProcess())
			{
				x.can_use_RavenDB_in_a_remote_process_to_post_batch_operations();
			}
		}
	}
}