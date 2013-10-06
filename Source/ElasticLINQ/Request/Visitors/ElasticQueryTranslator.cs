﻿// Copyright (c) Tier 3 Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. 

using ElasticLinq.Mapping;
using ElasticLinq.Request.Filters;
using ElasticLinq.Utility;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ElasticLinq.Request.Visitors
{
    internal class ElasticTranslateResult
    {
        public ElasticSearchRequest SearchRequest;
        public LambdaExpression Projector;
    }

    internal class FilterExpression : Expression
    {
        private readonly IFilter filter;

        public FilterExpression(IFilter filter)
        {
            this.filter = filter;
        }

        public IFilter Filter { get { return filter; } }

        public override ExpressionType NodeType
        {
            get { return (ExpressionType)10000; }
        }

        public override Type Type
        {
            get { return typeof(bool); }
        }
    }

    /// <summary>
    /// Expression visitor to translate a LINQ query into ElasticSearch request.
    /// </summary>
    internal class ElasticQueryTranslator : ExpressionVisitor
    {
        private readonly IElasticMapping mapping;
        private Projection projection;
        private ParameterExpression projectParameter;

        private readonly List<string> fields = new List<string>();
        private readonly List<SortOption> sortOptions = new List<SortOption>();
        private string type;
        private int skip;
        private int? take;
        private FilterExpression filterExpression;

        public ElasticQueryTranslator(IElasticMapping mapping)
        {
            this.mapping = mapping;
        }

        internal ElasticTranslateResult Translate(Expression e)
        {
            projectParameter = Expression.Parameter(typeof(JObject), "r");

            e = PartialEvaluator.Evaluate(e);
            Visit(e);

            if (projection != null)
                fields.AddRange(projection.Fields);

            return new ElasticTranslateResult
            {
                SearchRequest = new ElasticSearchRequest(type, skip, take, fields, sortOptions, filterExpression.Filter),
                Projector = projection != null ? Expression.Lambda(projection.Selector, projectParameter) : null
            };
        }

        protected override Expression VisitMethodCall(MethodCallExpression m)
        {
            if (m.Method.DeclaringType == typeof(Queryable))
                return VisitQueryableMethodCall(m);

            if (m.Method.DeclaringType == typeof(Enumerable))
                return VisitEnumerableMethodCall(m);

            if (m.Method.DeclaringType == typeof(ElasticQueryExtensions))
                return VisitElasticMethodCall(m);

            switch (m.Method.Name)
            {
                case "Equals":
                    if (m.Arguments.Count == 1)
                        return VisitEqualsMethodCall(m.Object, m.Arguments[0]) ?? m;
                    if (m.Arguments.Count == 2)
                        return VisitEqualsMethodCall(m.Arguments[0], m.Arguments[1]) ?? m;

                    break;

                case "Contains":
                    if (TypeHelper.FindIEnumerable(m.Method.DeclaringType) != null)
                        return VisitEnumerableContainsMethodCall(m.Object, m.Arguments[0]);
                    break;

                case "Create":
                    return m;
            }

            throw new NotSupportedException(string.Format("The method '{0}' is not supported", m.Method.Name));
        }

        private Expression VisitEnumerableMethodCall(MethodCallExpression m)
        {
            switch (m.Method.Name)
            {
                case "Contains":
                    if (m.Arguments.Count == 2)
                        return VisitEnumerableContainsMethodCall(m.Arguments[0], m.Arguments[1]);
                    break;
            }

            throw new NotSupportedException(string.Format("The Enumerable method '{0}' is not supported", m.Method.Name));
        }

        private Expression VisitEnumerableContainsMethodCall(Expression source, Expression match)
        {
            Visit(match);

            var constantSource = source as ConstantExpression;
            if (constantSource != null)
            {
                var field = mapping.GetFieldName(whereMemberInfos.Pop());
                var values = new List<object>(((IEnumerable)constantSource.Value).Cast<object>());
                filterExpression = new FilterExpression(new TermFilter(field, values.Distinct().ToArray()));
                return filterExpression;
            }

            throw new NotImplementedException("Unknown source for Contains");
        }

        internal Expression VisitEqualsMethodCall(Expression left, Expression right)
        {
            Visit(left);
            Visit(right);
            return MakeEquals();
        }

        internal Expression VisitElasticMethodCall(MethodCallExpression m)
        {
            switch (m.Method.Name)
            {
                case "OrderByScore":
                case "OrderByScoreDescending":
                case "ThenByScore":
                case "ThenByScoreDecending":
                    if (m.Arguments.Count == 1)
                        return VisitOrderByScore(m.Arguments[0], !m.Method.Name.EndsWith("Descending"));
                    break;
            }

            throw new NotSupportedException(string.Format("The ElasticQuery method '{0}' is not supported", m.Method.Name));
        }

        internal Expression VisitQueryableMethodCall(MethodCallExpression m)
        {
            switch (m.Method.Name)
            {
                case "Select":
                    if (m.Arguments.Count == 2)
                        return VisitSelect(m.Arguments[0], m.Arguments[1]);
                    break;
                case "Where":
                    if (m.Arguments.Count == 2)
                        return VisitWhere(m.Arguments[0], m.Arguments[1]);
                    break;
                case "Skip":
                    if (m.Arguments.Count == 2)
                        return VisitSkip(m.Arguments[0], m.Arguments[1]);
                    break;
                case "Take":
                    if (m.Arguments.Count == 2)
                        return VisitTake(m.Arguments[0], m.Arguments[1]);
                    break;
                case "OrderBy":
                case "OrderByDescending":
                    if (m.Arguments.Count == 2)
                        return VisitOrderBy(m.Arguments[0], m.Arguments[1], m.Method.Name == "OrderBy");
                    break;
                case "ThenBy":
                case "ThenByDescending":
                    if (m.Arguments.Count == 2)
                        return VisitOrderBy(m.Arguments[0], m.Arguments[1], m.Method.Name == "ThenBy");
                    break;
            }

            throw new NotSupportedException(string.Format("The Queryable method '{0}' is not supported", m.Method.Name));
        }

        protected override Expression VisitConstant(ConstantExpression c)
        {
            if (c.Value is IQueryable)
                SetType(((IQueryable)c.Value).ElementType);

            if (inWhereCondition)
                whereConstants.Push(c);

            return c;
        }

        protected override Expression VisitMember(MemberExpression m)
        {
            if (inWhereCondition)
                whereMemberInfos.Push(m.Member);

            if (m.Expression == null || m.Expression.NodeType != ExpressionType.Parameter)
                throw new NotSupportedException(string.Format("The memberInfo '{0}' is not supported", m.Member.Name));

            return m;
        }

        private static Expression StripQuotes(Expression e)
        {
            while (e.NodeType == ExpressionType.Quote)
                e = ((UnaryExpression)e).Operand;
            return e;
        }

        private bool inWhereCondition;
        private readonly Stack<MemberInfo> whereMemberInfos = new Stack<MemberInfo>();
        private readonly Stack<ConstantExpression> whereConstants = new Stack<ConstantExpression>();

        private Expression VisitWhere(Expression source, Expression predicate)
        {
            inWhereCondition = true; // TODO: Replace with context-sensitive stack

            var lambda = (LambdaExpression)StripQuotes(predicate);
            Visit(lambda);

            inWhereCondition = false;

            return Visit(source);
        }

        protected override Expression VisitBinary(BinaryExpression b)
        {
            switch (b.NodeType)
            {
                case ExpressionType.OrElse:
                    return VisitOrElse(b);

                case ExpressionType.AndAlso:
                    return VisitAndAlso(b);

                case ExpressionType.Equal:
                case ExpressionType.GreaterThan:
                case ExpressionType.GreaterThanOrEqual:
                case ExpressionType.LessThan:
                case ExpressionType.LessThanOrEqual:
                    return VisitComparisonBinary(b);

                default:
                    throw new NotImplementedException(String.Format("Don't yet know {0}", b.NodeType));
            }
        }

        private Expression VisitAndAlso(BinaryExpression b)
        {
            var filters = AssertExpressionsOfType<FilterExpression>(b.Left, b.Right).Select(f => f.Filter).ToArray();
            filterExpression = new FilterExpression(new AndFilter(filters));
            return filterExpression;
        }

        private Expression VisitOrElse(BinaryExpression b)
        {
            var filters = AssertExpressionsOfType<FilterExpression>(b.Left, b.Right).Select(f => f.Filter).ToArray();
            filterExpression = new FilterExpression(OrFilter.Combine(filters));
            return filterExpression;
        }

        private IEnumerable<T> AssertExpressionsOfType<T>(params Expression[] expressions) where T : Expression
        {
            foreach (var expression in expressions.Select(Visit))
                if ((expression as T) == null)
                    throw new NotImplementedException(string.Format("Unknown binary expression {0}", expression));
                else
                    yield return (T)expression;
        }

        private Expression VisitComparisonBinary(BinaryExpression b)
        {
            Visit(b.Left);
            Visit(b.Right);

            switch (b.NodeType)
            {
                case ExpressionType.Equal:
                    return MakeEquals() ?? b;

                case ExpressionType.GreaterThan:
                case ExpressionType.GreaterThanOrEqual:
                case ExpressionType.LessThan:
                case ExpressionType.LessThanOrEqual:
                    return MakeRange(b.NodeType) ?? b;

                default:
                    throw new NotImplementedException(String.Format("Don't yet know {0}", b.NodeType));
            }
        }

        private Expression MakeRange(ExpressionType t)
        {
            var haveMemberAndConstant = whereMemberInfos.Any() && whereConstants.Any();

            if (haveMemberAndConstant)
            {
                var field = mapping.GetFieldName(whereMemberInfos.Pop());
                var specification = new RangeSpecificationFilter(ExpressionTypeToRangeType(t), whereConstants.Pop().Value);
                var expression = new FilterExpression(new RangeFilter(field, specification));
                filterExpression = expression;
                return expression;
            }

            return null;
        }

        private static string ExpressionTypeToRangeType(ExpressionType t)
        {
            switch (t)
            {
                case ExpressionType.GreaterThan:
                    return "gt";
                case ExpressionType.GreaterThanOrEqual:
                    return "gte";
                case ExpressionType.LessThan:
                    return "lt";
                case ExpressionType.LessThanOrEqual:
                    return "lte";
            }

            throw new ArgumentOutOfRangeException("t");
        }

        private Expression MakeEquals()
        {
            var haveMemberAndConstant = whereMemberInfos.Any() && whereConstants.Any();

            if (haveMemberAndConstant)
            {
                var field = mapping.GetFieldName(whereMemberInfos.Pop());
                var expression = new FilterExpression(new TermFilter(field, whereConstants.Pop().Value));
                if (filterExpression == null)
                    filterExpression = expression;
                return expression;
            }

            return null;
        }

        private Expression VisitOrderBy(Expression source, Expression orderByExpression, bool ascending)
        {
            var lambda = (LambdaExpression)StripQuotes(orderByExpression);
            var final = Visit(lambda.Body) as MemberExpression;
            if (final != null)
            {
                var fieldName = mapping.GetFieldName(final.Member);
                sortOptions.Insert(0, new SortOption(fieldName, ascending));
            }

            return Visit(source);
        }

        private Expression VisitOrderByScore(Expression source, bool ascending)
        {
            sortOptions.Insert(0, new SortOption("_score", ascending));
            return Visit(source);
        }

        private Expression VisitSelect(Expression source, Expression selectExpression)
        {
            var lambda = (LambdaExpression)StripQuotes(selectExpression);
            var selectBody = Visit(lambda.Body);

            if (selectBody is NewExpression || selectBody is MemberExpression || selectBody is MethodCallExpression)
                VisitSelectNew(selectBody);

            return Visit(source);
        }

        private void VisitSelectNew(Expression selectBody)
        {
            projection = ProjectionVisitor.ProjectColumns(projectParameter, mapping, selectBody);
        }

        private Expression VisitSkip(Expression source, Expression skipExpression)
        {
            var skipConstant = Visit(skipExpression) as ConstantExpression;
            if (skipConstant != null)
                skip = (int)skipConstant.Value;
            return Visit(source);
        }

        private Expression VisitTake(Expression source, Expression takeExpression)
        {
            var takeConstant = Visit(takeExpression) as ConstantExpression;
            if (takeConstant != null)
                take = (int)takeConstant.Value;
            return Visit(source);
        }

        private void SetType(Type elementType)
        {
            type = elementType == typeof(object) ? null : mapping.GetTypeName(elementType);
        }
    }
}