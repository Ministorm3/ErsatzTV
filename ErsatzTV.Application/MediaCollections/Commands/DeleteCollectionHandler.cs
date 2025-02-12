﻿using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.MediaCollections;

public class DeleteCollectionHandler : IRequestHandler<DeleteCollection, Either<BaseError, Unit>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public DeleteCollectionHandler(IDbContextFactory<TvContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<Either<BaseError, Unit>> Handle(
        DeleteCollection request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = _dbContextFactory.CreateDbContext();

        Validation<BaseError, Collection> validation = await CollectionMustExist(dbContext, request);
        return await LanguageExtensions.Apply(validation, c => DoDeletion(dbContext, c));
    }

    private static Task<Unit> DoDeletion(TvContext dbContext, Collection collection)
    {
        dbContext.Collections.Remove(collection);
        return dbContext.SaveChangesAsync().ToUnit();
    }

    private static Task<Validation<BaseError, Collection>> CollectionMustExist(
        TvContext dbContext,
        DeleteCollection request) =>
        dbContext.Collections
            .SelectOneAsync(c => c.Id, c => c.Id == request.CollectionId)
            .Map(o => o.ToValidation<BaseError>($"Collection {request.CollectionId} does not exist."));
}
