﻿using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Client;
using Raven.Client.Embedded;
using Raven.Client.Indexes;
using Raven.Client.Listeners;
using Xunit;

namespace Raven.Tests.MailingList
{

	public class ChrisDNF : IDisposable
	{
		[Fact]
		public void Test()
		{
			using (var session = documentStore.OpenSession())
			{
				var category = new Category() { Colour = "green", Name = "c1" };
				session.Store(category);
				var activity = new Activity() { Name = "a1", Category = category };
				session.Store(activity);
				session.SaveChanges();
				category.Colour = "yellow";
				session.Store(category);
				session.SaveChanges();
				session.ClearStaleIndexes();
				UpdateLinkedIndexes(category, new[] { "Activity/ByCategoryId" }, new[] { "Colour" });
			}
			using (var session = documentStore.OpenSession())
			{
				var a2 = session.Load<Activity>("activities/1");
				Assert.Equal("yellow", a2.Category.Colour);
			}
		}

		protected void UpdateLinkedIndexes<T>(T entity, string[] indexes, string[] properties) where T : IHasId
		{
			var type = typeof(T);
			var dnfproperty = type.Name;  // "Category"

			var prs = properties.Select(p =>
			{
				var d = new DynamicPropertyAccessor<T, string>(typeof(T).GetProperty(p));
				return new PatchRequest()
				{
					Type = PatchCommandType.Set,
					Name = p,         // "Colour"
					Value = d[entity]    // "yellow"
				};
			}).ToArray();

			var pr_outer = new PatchRequest()
			{
				Type = PatchCommandType.Modify,
				Name = dnfproperty,
				Nested = prs
			};

			var query = new IndexQuery { Query = "CategoryId" + ":" + entity.Id };

			foreach (var index in indexes)
				documentStore.DatabaseCommands.UpdateByIndex(index, query, new PatchRequest[] { pr_outer }, false);
		}


		protected EmbeddableDocumentStore documentStore;

		public ChrisDNF()
		{
			documentStore = new EmbeddableDocumentStore
			{
				RunInMemory = true
			};
			documentStore.RegisterListener(new NoStaleQueriesAllowed());
			documentStore.Initialize();
			
			new ActivityIndexes.Activity_ByCategoryId().Execute(documentStore);	

			AfterInit();
		}

		public class NoStaleQueriesAllowed : IDocumentQueryListener
		{
			public void BeforeQueryExecuted(IDocumentQueryCustomization queryCustomization)
			{
				queryCustomization.WaitForNonStaleResults();
			}
		}

		public virtual void Dispose()
		{
			documentStore.Dispose();
		}

		public virtual void AfterInit()
		{

		}
	}

	public class DenormalizedReference<T> where T : INamedDocument
	{
		public string Id { get; set; }
		public string Name { get; set; }

		public static implicit operator DenormalizedReference<T>(T doc)
		{
			return new DenormalizedReference<T>
			{
				Id = doc.Id,
				Name = doc.Name
			};

		}
	}

	public interface IHasId
	{
		string Id { get; set; }
	}

	public interface INamedDocument
	{
		string Id { get; set; }
		string Name { get; set; }
	}
	public class Activity : INamedDocument, IHasId
	{
		public string Id { get; set; }
		public string Name { get; set; }
		public bool Favourite { get; set; }
		public CategoryReference Category { get; set; }
	}

	public class Category : INamedDocument, IHasId
	{
		public string Id { get; set; }
		public string Name { get; set; }
		public string Colour { get; set; }
	}

	public class CategoryReference : DenormalizedReference<Category>
	{
		public string Colour { get; set; }

		public static implicit operator CategoryReference(Category doc)
		{
			return new CategoryReference
			{
				Id = doc.Id,
				Name = doc.Name,
				Colour = doc.Colour
			};
		}
	}

	public class ActivityIndexes
	{
		public class Activity_ByCategoryId : AbstractIndexCreationTask<Activity>
		{
			public Activity_ByCategoryId()
			{
				Map = activities => from activity in activities
									select new { CategoryId = activity.Category.Id };
			}
		}
	}

	public static class IDocumentSessionExtensions
	{
		public static void ClearStaleIndexes(this IDocumentSession db)
		{
			while (db.Advanced.DocumentStore.DatabaseCommands.GetStatistics().StaleIndexes.Length != 0)
			{
				Thread.Sleep(10);
			}
		}
	}

	public class DynamicPropertyAccessor<TObject, TProperty>
	{
		public string Name { get; private set; }
		public Type PropertyType { get; private set; }

		private Lazy<Func<TObject, TProperty>> Getter { get; set; }
		private Lazy<Action<TObject, TProperty>> Setter { get; set; }

		public DynamicPropertyAccessor(PropertyInfo targetProperty)
		{

			Name = targetProperty.Name;
			PropertyType = targetProperty.PropertyType;

			var objectParam = Expression.Parameter(typeof(TObject), "object");
			var specificObject = Expression.Convert(objectParam, targetProperty.ReflectedType);
			var property = Expression.Property(specificObject, targetProperty);

			if (targetProperty.CanRead)
			{
				// Getter
				var convertToObject = Expression.Convert(property, typeof(TProperty));
				var getter = Expression.Lambda<Func<TObject, TProperty>>(convertToObject, objectParam);
				Getter = new Lazy<Func<TObject, TProperty>>(getter.Compile);
			}

			if (targetProperty.CanWrite)
			{
				// Setter
				var valueParam = Expression.Parameter(typeof(TProperty), "value");
				var value = Expression.Convert(valueParam, PropertyType);
				var assign = Expression.Assign(property, value);
				var setter = Expression.Lambda<Action<TObject, TProperty>>(assign, objectParam, valueParam);
				Setter = new Lazy<Action<TObject, TProperty>>(setter.Compile);
			}
		}

		public TProperty this[TObject obj]
		{
			get
			{


				if (Getter == null)
					throw new InvalidOperationException(string.Format("The property '{0}', represented by this DynamicPropertyAccessor, is not readable.", Name));

				return Getter.Value(obj);
			}
			set
			{


				if (Setter == null)
					throw new InvalidOperationException(string.Format("The property '{0}', represented by this DynamicPropertyAccessor, is not writeable.", Name));

				Setter.Value(obj, value);
			}
		}
	}
	public class DynamicPropertyAccessor : DynamicPropertyAccessor<object, object>
	{
		public DynamicPropertyAccessor(PropertyInfo targetProperty) : base(targetProperty) { }
	}



}