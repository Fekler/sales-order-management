﻿using Mapster;
using Microsoft.Extensions.Logging;
using SalesOrderManagement.Application.Dtos.Entities.Order;
using SalesOrderManagement.Application.Dtos.Entities.OrderItem;
using SalesOrderManagement.Application.Dtos.Entities.Product;
using SalesOrderManagement.Application.Interfaces.Business;
using SalesOrderManagement.Application.Interfaces.UseCases;
using SalesOrderManagement.Domain.Interfaces.Repositories;
using SharedKernel.Utils;
using System.Net;
using static SalesOrderManagement.Domain.Entities._bases.Enums;

namespace SalesOrderManagement.Application.UseCases
{
    public class OrderProcessing : IOrderProcessing
    {
        private readonly IOrderBusiness _orderBusiness;
        private readonly IProductBusiness _productBusiness;
        private readonly IOrderItemBusiness _orderItemBusiness;
        private readonly IUserBusiness _userBusiness;
        private readonly ILogger<OrderProcessing> _logger;

        public OrderProcessing(IOrderBusiness orderBusiness, IProductBusiness productBusiness, IOrderItemBusiness orderItemBusiness, IUserBusiness userBusiness, ILogger<OrderProcessing> logger)
        {
            _orderBusiness = orderBusiness;
            _productBusiness = productBusiness;
            _orderItemBusiness = orderItemBusiness;
            _userBusiness = userBusiness;
            _logger = logger;
        }

        public async Task<Response<Guid>> CreateOrder(CreateOrderDto createOrderDto)
        {
            try
            {
                var orderResult = await _orderBusiness.Add(createOrderDto);
                if (!orderResult.ApiReponse.Success)
                {
                    return new Response<Guid>().Failure(default, message: orderResult.ApiReponse.Message, statusCode: orderResult.StatusCode);
                }
                var orderUuid = orderResult.ApiReponse.Data;
                var orderEntityResult = await _orderBusiness.GetEntity(orderUuid);
                if (!orderEntityResult.ApiReponse.Success || orderEntityResult.ApiReponse.Data == null)
                {
                    return new Response<Guid>().Failure(default, message: "Erro ao recuperar a entidade do pedido.", statusCode: HttpStatusCode.InternalServerError);
                }
                var orderEntity = orderEntityResult.ApiReponse.Data;

                //await _orderBusiness.Update(orderEntity.Adapt<UpdateOrderDto>());

                decimal totalOrderAmount = 0;
                var createdOrderItemsUuids = new List<Guid>();


                foreach (var itemDto in createOrderDto.OrderItems)
                {
                    var productResult = await _productBusiness.GetEntity(itemDto.ProductId);
                    if (!productResult.ApiReponse.Success || productResult.ApiReponse.Data == null)
                    {
                        return new Response<Guid>().Failure(default, message: $"Produto com UUID: {itemDto.ProductId} não encontrado.", statusCode: HttpStatusCode.NotFound);
                    }
                    var product = productResult.ApiReponse.Data;

                    if (!itemDto.OrderId.HasValue || itemDto.OrderId.Value != orderUuid)
                    {
                        itemDto.OrderId = orderUuid;
                    }
                    var createOrderItemResult = await _orderItemBusiness.Add(itemDto);
                    if (!createOrderItemResult.ApiReponse.Success)
                    {

                        return new Response<Guid>().Failure(default, message: $"Erro ao adicionar item ao pedido: {createOrderItemResult.ApiReponse.Message}", statusCode: createOrderItemResult.StatusCode);
                    }
                    var orderItemUuid = createOrderItemResult.ApiReponse.Data;
                    var orderItemEntityResult = await _orderItemBusiness.GetEntity(orderItemUuid);
                    if (!orderItemEntityResult.ApiReponse.Success || orderItemEntityResult.ApiReponse.Data == null)
                    {
                        return new Response<Guid>().Failure(default, message: "Erro ao recuperar a entidade do item do pedido.", statusCode: HttpStatusCode.InternalServerError);
                    }
                    var orderItemEntity = orderItemEntityResult.ApiReponse.Data;

                    totalOrderAmount += orderItemEntity.TotalPrice;
                    createdOrderItemsUuids.Add(orderItemUuid);
                }
                var orderEntityResultWithItemsResponse = await _orderBusiness.GetEntity(orderEntityResult.ApiReponse.Data.UUID);

                var orderEntityResultWithItems = orderEntityResultWithItemsResponse.ApiReponse.Data;
                orderEntityResultWithItems?.CalculateTotalAmount();
                await _orderBusiness.Update(orderEntityResultWithItems.Adapt<UpdateOrderDto>());

                return new Response<Guid>().Sucess(orderUuid, message: "Pedido criado com sucesso.", statusCode: HttpStatusCode.Created);
            }
            catch (Exception ex)
            {
                return new Response<Guid>().Failure(default, message: $"Erro ao processar a criação do pedido: {ex.Message}", statusCode: HttpStatusCode.InternalServerError);
            }
        }

        public async Task<Response<IEnumerable<OrderDto>>> GetAllByLoggedUser(Guid userUuid)
        {

            try
            {
                var user = await _userBusiness.GetEntity(userUuid);
                if (!user.ApiReponse.Success || user.ApiReponse.Data == null)
                {
                    return new Response<IEnumerable<OrderDto>>().Failure(default, message: "Usuário não encontrado.", statusCode: HttpStatusCode.NotFound);
                }

                return user.ApiReponse.Data.UserRole switch
                {
                    UserRole.Admin => await _orderBusiness.GetAllWithItemsAsync(),
                    UserRole.Seller => await _orderBusiness.GetAllWithItemsAsync(),
                    UserRole.Client => await _orderBusiness.GetOrdersByUserId(userUuid),
                    _ => new Response<IEnumerable<OrderDto>>().Failure(default, message: "Função de usuário inválida.", statusCode: HttpStatusCode.BadRequest),
                };
            }
            catch (Exception)
            {

                throw;
            }
        }

        public async Task<Response<bool>> ActionOrder(Guid orderUuid, Guid userUuid, OrderStatus orderStatus)
        {
            try
            {
                var orderResult = await _orderBusiness.GetEntity(orderUuid);
                if (!orderResult.ApiReponse.Success || orderResult.ApiReponse.Data == null)
                {
                    return new Response<bool>().Failure(false, message: "Pedido não encontrado.", statusCode: HttpStatusCode.NotFound);
                }
                var order = orderResult.ApiReponse.Data;
                var userResult = await _userBusiness.GetEntity(userUuid);
                if (orderResult.ApiReponse.Data.Status == OrderStatus.Approved)
                {
                    return new Response<bool>().Failure(false, message: "Pedido já foi aprovado.", statusCode: HttpStatusCode.BadRequest);
                }
                if (!userResult.ApiReponse.Success || userResult.ApiReponse.Data == null)
                {
                    return new Response<bool>().Failure(false, message: "Usuário não encontrado.", statusCode: HttpStatusCode.NotFound);
                }
                var user = userResult.ApiReponse.Data;
                if (user.UserRole != UserRole.Admin && user.UserRole != UserRole.Seller)
                {
                    return new Response<bool>().Failure(false, message: "Apenas administradores e vendedores podem alterar o status do pedido.", statusCode: HttpStatusCode.Forbidden);
                }
                order.Status = orderStatus;
                order.ActionedByUserUuid = user.UUID;
                order.ActionedAt = DateTime.UtcNow;
                if (orderStatus == OrderStatus.Approved)
                {
                    foreach (var item in order.OrderItems)
                    {
                        var orderItemResult = await _orderItemBusiness.GetEntity(item.UUID);
                        if (!orderItemResult.ApiReponse.Success || orderItemResult.ApiReponse.Data == null)
                        {
                            return new Response<bool>().Failure(false, message: "Item do pedido não encontrado.", statusCode: HttpStatusCode.NotFound);
                        }
                        var orderItem = orderItemResult.ApiReponse.Data;
                        if (orderItem.Product.Quantity < item.Quantity)
                        {
                            order.Status = OrderStatus.InsuficientProducts;
                            await _orderBusiness.Update(order.Adapt<UpdateOrderDto>());
                            _logger.LogWarning($"Produto {orderItem.Product.Name} não tem estoque suficiente. Pedido: {order.UUID}, Item: {orderItem.UUID}");
                            return new Response<bool>().Failure(false, message: $"Produto {orderItem.Product.Name} não tem estoque suficiente.", statusCode: HttpStatusCode.BadRequest);
                        }
                        orderItem.Product.Quantity -= item.Quantity;
                        await _productBusiness.Update(orderItem.Product.Adapt<UpdateProductDto>());
                        await _orderItemBusiness.Update(orderItem.Adapt<UpdateOrderItemDto>());
                    }
                }
                await _orderBusiness.Update(order.Adapt<UpdateOrderDto>());

                return new Response<bool>().Sucess(true, message: "Status do pedido atualizado com sucesso.", statusCode: HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                return new Response<bool>().Failure(false, message: $"Erro ao processar a ação no pedido: {ex.Message}", statusCode: HttpStatusCode.InternalServerError);
            }
        }
    }

}