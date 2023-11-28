using MediatR;
using my_app_backend.Application.Commands;
using my_app_backend.Domain.AggregateModel.BookAggregate;
using my_app_backend.Domain.SeedWork.Models;

namespace my_app_backend.Application.CommandHandlers
{
    public class UpdateBookCommandHandler : IRequestHandler<UpdateBookCommand, Result>
    {
        private readonly IBookEventStore _bookEventStore;
        public UpdateBookCommandHandler(IBookEventStore bookEventStore)
        {
            _bookEventStore = bookEventStore;
        }
        public async Task<Result> Handle(UpdateBookCommand request, CancellationToken cancellationToken)
        {
            try
            {
                var aggregate = await _bookEventStore.Get(request.Id);
                aggregate.UpdateBook(request.Name, request.Author, request.Type, request.Locked);
                await _bookEventStore.Save(aggregate);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Error($"Exception happened: {ex}");
            }
        }
    }
}
