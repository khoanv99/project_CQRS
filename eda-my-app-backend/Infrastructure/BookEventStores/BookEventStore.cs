﻿using Dapper;
using MediatR;
using Microsoft.Extensions.Options;
using my_app_backend.Domain.AggregateModel.BookAggregate;
using my_app_backend.Domain.AggregateModel.BookAggregate.Events;
using my_app_backend.Infrastructure.QueryRepositories;
using Newtonsoft.Json;
using System.Data.SqlClient;
using System.Diagnostics;

namespace my_app_backend.Infrastructure.Store
{
    public class BookEventStore : IBookEventStore
    {
        private string _writeConnectionString;
        private readonly IMediator _mediator;

        public BookEventStore(IOptions<BookWriteDatabaseSettings> bookStoreDatabaseSettings, IMediator mediator)
        {
            _writeConnectionString = bookStoreDatabaseSettings.Value.ConnectionString;
            _mediator = mediator;
        }

        public async Task<BookAggregate> Get(Guid id)
        {
            using (var connection = new SqlConnection(_writeConnectionString))
            {
                await connection.OpenAsync();

                var query = "SELECT * FROM BookAggregate WHERE Id = @id";
                var aggregate = await connection.QueryFirstOrDefaultAsync<BookAggregate>(query, new { id });
                if (aggregate == null)
                {
                    throw new Exception($"Not found for book aggregate id = {id}");
                }
                BookAggregateFactory.InitState(aggregate);

                query = "SELECT * FROM BookEvent WHERE AggregateId = @id ORDER BY Version";
                var serealizedEvents = await connection.QueryAsync<BookSerializedEvent>(query, new { id });
                var events = new List<IBookEvent>();
                foreach (var se in serealizedEvents)
                {
                    var @event = JsonConvert.DeserializeObject(se.Data, Type.GetType(se.Type)) as IBookEvent;
                    @event.Id = se.Id;
                    @event.AggregateId = se.AggregateId;
                    @event.Version = se.Version;
                    @event.CreateDate = se.CreateDate;

                    events.Add(@event);
                }
                aggregate.Rehydate(events);

                return aggregate;
            }
        }

        public async Task Save(BookAggregate aggregate)
        {
            using (var connection = new SqlConnection(_writeConnectionString))
            {
                await connection.OpenAsync();
                var aggregateQuery = "SELECT Id, Version FROM BookAggregate WHERE Id = @id";
                var dbAggregate = await connection.QueryFirstOrDefaultAsync<BookAggregate>(aggregateQuery, new { id = aggregate.Id });
                if (dbAggregate != null && dbAggregate.Version != aggregate.Version)
                {
                    throw new Exception($"Book aggregate is updated to version: {dbAggregate.Version}, current is: {aggregate.Version}");
                }

                var trans = connection.BeginTransaction();
                try
                {
                    var events = aggregate.Flush();

                    if (dbAggregate == null)
                    {
                        var insertAggregateCmd = "INSERT INTO post_write.dbo.BookAggregate(Id, Version, CreatedDate, ModifiedDate) VALUES (@Id, @Version, @CreatedDate, @ModifiedDate);";
                        await connection.ExecuteAsync(insertAggregateCmd, aggregate, transaction: trans);
                    }
                    else
                    {
                        var updateAggregateCmd = "UPDATE BookAggregate SET Version=@Version, ModifiedDate=@ModifiedDate WHERE Id=@Id;";
                        await connection.ExecuteAsync(updateAggregateCmd, aggregate, transaction: trans);
                    }

                    var serealizedEvents = events.Select(e => new BookSerializedEvent
                    {
                        Id = e.Id,
                        Version = e.Version,
                        AggregateId = e.AggregateId,
                        Data = JsonConvert.SerializeObject(e),
                        Type = e.GetType().AssemblyQualifiedName,
                        CreateDate = e.CreateDate
                    }).ToList();
                    var insertEventCmd = "INSERT INTO post_write.dbo.BookEvent (Id, AggregateId, Version, CreateDate, [Data], [Type]) VALUES(@Id, @AggregateId, @Version, @CreateDate, @Data, @Type);";
                    foreach (var se in serealizedEvents)
                    {
                        await connection.ExecuteAsync(insertEventCmd, se, transaction: trans);
                    }

                    foreach (var e in events)
                    {
                        await _mediator.Publish(e);
                    }

                    trans.Commit();
                }
                catch
                {
                    trans.Rollback();
                    throw;
                }
            }
        }

        public async Task Delete(BookAggregate aggregate)
        {
            using (var connection = new SqlConnection(_writeConnectionString))
            {
                try
                {
                    await connection.OpenAsync();
                    var deleteEventsQuery = "DELETE FROM BookEvent WHERE AggregateId = @id;";
                    await connection.ExecuteAsync(deleteEventsQuery, new { aggregate.Id });

                    var deleteAggregateQuery = "DELETE FROM BookAggregate WHERE Id = @id;";
                    await connection.ExecuteAsync(deleteAggregateQuery, new { aggregate.Id });
                    var events = aggregate.Flush();
                    foreach (var e in events)
                    {
                        await _mediator.Publish(e);
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Book aggregate is delete : {ex}");
                }
            }
        }
    }
}
