﻿using NetworkSocket.Http;
using NetworkSocket.Tasks;
using NetworkSocket.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkSocket.WebSocket
{
    /// <summary>
    /// 表示WebSocket中间件抽象类
    /// 只支持 RFC 6455 协议
    /// </summary>
    public abstract class WebSocketMiddlewareBase : IMiddleware
    {
        /// <summary>
        /// 下一个中间件
        /// </summary>
        public IMiddleware Next { get; set; }

        /// <summary>
        /// 执行中间件
        /// </summary>
        /// <param name="context">上下文</param>
        /// <returns></returns>
        bool IMiddleware.Invoke(IContenxt context)
        {
            var protocol = context.Session.Protocol;
            if (protocol == Protocol.WebSocket)
            {
                return this.OnWebSocketFrameRequest(context);
            }

            if (protocol == Protocol.None || protocol == Protocol.Http)
            {
                return this.OnWebSocketHandshakeRequest(context);
            }
            else
            {
                return this.Next.Invoke(context);
            }
        }

        /// <summary>
        /// 收到WebSocket的握手请求
        /// </summary>
        /// <param name="context">上下文</param>
        /// <returns></returns>
        private bool OnWebSocketHandshakeRequest(IContenxt context)
        {
            try
            {
                var result = HttpRequestParser.Parse(context);
                if (result.IsHttp == false)
                {
                    return this.Next.Invoke(context);
                }

                // 数据未完整
                if (result.Request == null)
                {
                    return true;
                }

                if (result.Request.IsWebsocketRequest() == false)
                {
                    return this.Next.Invoke(context);
                }

                context.InputStream.Clear(result.PackageLength);
                const string seckey = "Sec-WebSocket-Key";
                var secValue = result.Request.Headers[seckey];
                this.ResponseHandshake(context, secValue);
                return true;
            }
            catch (Exception)
            {
                context.InputStream.Clear();
                context.Session.Close();
                return false;
            }
        }

        /// <summary>
        /// 回复握手请求
        /// </summary>
        /// <param name="context">上下文</param>
        /// <param name="secValue">Sec-WebSocket-Key</param>
        private void ResponseHandshake(IContenxt context, string secValue)
        {
            var wrapper = new WebSocketSession(context.Session);
            var hansshakeResponse = new HandshakeResponse(secValue);

            if (wrapper.TrySend(hansshakeResponse) == true)
            {
                this.OnSetProtocolWrapper(context.Session, wrapper);
            }
        }

        /// <summary>
        /// 设置会话的包装对象
        /// </summary>
        /// <param name="session">会话</param>
        /// <param name="wrapper">包装对象</param>
        protected virtual void OnSetProtocolWrapper(ISession session, WebSocketSession wrapper)
        {
            session.SetProtocolWrapper(Protocol.WebSocket, wrapper);
        }

        /// <summary>
        /// 收到WebSocket请求
        /// </summary>
        /// <param name="context">上下文</param>
        /// <returns></returns>
        private bool OnWebSocketFrameRequest(IContenxt context)
        {
            var requests = this.GenerateWebSocketRequest(context);
            foreach (var request in requests)
            {
                this.OnWebSocketRequest(context, request);
            }
            return true;
        }

        /// <summary>
        /// 解析生成请求帧
        /// </summary>
        /// <param name="context">上下文</param>
        /// <returns></returns>
        private IList<FrameRequest> GenerateWebSocketRequest(IContenxt context)
        {
            var list = new List<FrameRequest>();
            while (true)
            {
                try
                {
                    var request = FrameRequest.Parse(context.InputStream);
                    if (request == null)
                    {
                        return list;
                    }
                    list.Add(request);
                }
                catch (NotSupportedException)
                {
                    context.Session.Close();
                    return list;
                }
            }
        }

        /// <summary>
        /// 收到到数据帧请求
        /// </summary>
        /// <param name="context">会话对象</param>
        /// <param name="frameRequest">数据帧</param>
        private void OnWebSocketRequest(IContenxt context, FrameRequest frameRequest)
        {
            switch (frameRequest.Frame)
            {
                case FrameCodes.Close:
                    var reason = StatusCodes.NormalClosure;
                    if (frameRequest.Content.Length > 1)
                    {
                        var status = ByteConverter.ToUInt16(frameRequest.Content, 0, Endians.Big);
                        reason = (StatusCodes)status;
                    }
                    this.OnClose(context, reason);
                    context.Session.Close();
                    break;

                case FrameCodes.Binary:
                    this.OnBinary(context, frameRequest.Content);
                    break;

                case FrameCodes.Text:
                    var content = Encoding.UTF8.GetString(frameRequest.Content);
                    this.OnText(context, content);
                    break;

                case FrameCodes.Ping:
                    try
                    {
                        var session = (WebSocketSession)context.Session.Wrapper;
                        session.Send(new FrameResponse(FrameCodes.Pong, frameRequest.Content));
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        this.OnPing(context, frameRequest.Content);
                    }
                    break;

                case FrameCodes.Pong:
                    this.OnPong(context, frameRequest.Content);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// 收到文本请求类型时触发此方法
        /// </summary>
        /// <param name="context">会话对象</param>
        /// <param name="content">文本内容</param>
        protected virtual void OnText(IContenxt context, string content)
        {
        }

        /// <summary>
        /// 收到二进制类型请求时触发此方法
        /// </summary>
        /// <param name="context">会话对象</param>
        /// <param name="content">二进制内容</param>
        protected virtual void OnBinary(IContenxt context, byte[] content)
        {
        }

        /// <summary>
        /// 收到Ping请求时触发此方法
        /// 在触发此方法之前，基础服务已自动将Pong回复此会话
        /// </summary>
        /// <param name="context">会话对象</param>
        /// <param name="content">二进制内容</param>
        protected virtual void OnPing(IContenxt context, byte[] content)
        {
        }

        /// <summary>
        /// Ping后会话对象将回复Pong触发此方法
        /// </summary>
        /// <param name="context">上下文</param>
        /// <param name="content">二进制内容</param>
        protected virtual void OnPong(IContenxt context, byte[] content)
        {
        }

        /// <summary>
        /// 收到会话的关闭信息
        /// 在触发此方法后，基础服务将自动安全回收此会话对象
        /// </summary>
        /// <param name="context">上下文</param>
        /// <param name="code">关闭码</param>
        protected virtual void OnClose(IContenxt context, StatusCodes code)
        {
        }
    }
}
