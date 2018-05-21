﻿using System;
using System.Linq;
using System.Threading.Tasks;
using GraphQL.Types;
using PlumMediaCenter.Graphql.Mutations;

namespace PlumMediaCenter.Graphql
{
    public class RootMutationGraphType : ObjectGraphType
    {
        public RootMutationGraphType(
            SourceMutations sourceMutations,
            LibraryMutations libraryMutations,
            MovieMetadataMutations movieMetadataMutations
        )
        {
            this.Name = "Mutation";
            sourceMutations.Register(this);
            libraryMutations.Register(this);
            movieMetadataMutations.Register(this);
        }
    }
}