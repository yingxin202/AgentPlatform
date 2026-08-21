/**
 * AgentPlatform - AI Agent Chat Platform
 * Main Application Logic
 */

(function () {
    'use strict';

    // ============================================
    //  State Management
    // ============================================
    const state = {
        currentSessionId: null,
        sessions: [],
        messages: [],
        settings: null,
        mcpServers: [],
        skills: [],
        mcpTools: [],
        isStreaming: false,
        abortController: null,
        pendingImages: [],
        currentAssistantMessageEl: null,
        currentAssistantContent: '',
        currentToolCalls: []
    };

    // ============================================
    //  API Helper
    // ============================================
    const API = {
        async get(url) {
            const res = await fetch(url);
            if (!res.ok) throw new Error(`HTTP ${res.status}: ${res.statusText}`);
            return res.json();
        },
        async post(url, body) {
            const res = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: body ? JSON.stringify(body) : undefined
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}: ${res.statusText}`);
            return res.json();
        },
        async put(url, body) {
            const res = await fetch(url, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: body ? JSON.stringify(body) : undefined
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}: ${res.statusText}`);
            return res.json();
        },
        async del(url) {
            const res = await fetch(url, { method: 'DELETE' });
            if (!res.ok) throw new Error(`HTTP ${res.status}: ${res.statusText}`);
            return res.json();
        }
    };

    // ============================================
    //  DOM Helpers
    // ============================================
    const $ = (id) => document.getElementById(id);
    const el = (tag, className, text) => {
        const e = document.createElement(tag);
        if (className) e.className = className;
        if (text) e.textContent = text;
        return e;
    };

    // ============================================
    //  Toast Notifications
    // ============================================
    function showToast(message, type = 'info') {
        const container = $('toast-container');
        const toast = el('div', `toast toast-${type}`);

        const icons = {
            success: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg>',
            error: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><line x1="15" y1="9" x2="9" y2="15"/><line x1="9" y1="9" x2="15" y2="15"/></svg>',
            info: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/></svg>'
        };

        toast.innerHTML = icons[type] || icons.info;
        const span = el('span', null, message);
        toast.appendChild(span);
        container.appendChild(toast);

        setTimeout(() => {
            toast.classList.add('fade-out');
            setTimeout(() => toast.remove(), 300);
        }, 3500);
    }

    // ============================================
    //  Markdown Rendering
    // ============================================
    function renderMarkdown(text) {
        if (!text) return '';
        try {
            if (typeof marked !== 'undefined') {
                marked.setOptions({
                    breaks: true,
                    gfm: true,
                    highlight: function (code, lang) {
                        if (typeof hljs !== 'undefined' && lang && hljs.getLanguage(lang)) {
                            try {
                                return hljs.highlight(code, { language: lang }).value;
                            } catch (e) { /* fall through */ }
                        }
                        if (typeof hljs !== 'undefined') {
                            try {
                                return hljs.highlightAuto(code).value;
                            } catch (e) { /* fall through */ }
                        }
                        return code;
                    }
                });
                return marked.parse(text);
            }
        } catch (e) {
            console.error('Markdown render error:', e);
        }
        return escapeHtml(text).replace(/\n/g, '<br>');
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // ============================================
    //  Session Management
    // ============================================
    async function loadSessions() {
        try {
            state.sessions = await API.get('/api/chat/sessions');
            renderSessionList();
        } catch (e) {
            console.error('Failed to load sessions:', e);
            showToast('加载对话列表失败', 'error');
        }
    }

    async function createNewSession() {
        try {
            const data = await API.post('/api/chat/session');
            state.currentSessionId = data.sessionId;
            state.messages = [];
            await loadSessions();
            renderMessages();
            updateChatHeader('新对话');
            $('message-input').focus();
        } catch (e) {
            console.error('Failed to create session:', e);
            showToast('创建对话失败', 'error');
        }
    }

    async function switchSession(sessionId) {
        if (state.isStreaming) {
            showToast('请先停止当前生成', 'info');
            return;
        }
        state.currentSessionId = sessionId;
        try {
            state.messages = await API.get(`/api/chat/messages/${sessionId}`);
            renderMessages();
            renderSessionList();

            const session = state.sessions.find(s => s.id === sessionId);
            updateChatHeader(session ? session.title : '对话');
        } catch (e) {
            console.error('Failed to load messages:', e);
            showToast('加载消息失败', 'error');
        }
    }

    async function deleteSession(sessionId) {
        if (!confirm('确定删除此对话吗？')) return;
        try {
            await API.del(`/api/chat/session/${sessionId}`);
            if (state.currentSessionId === sessionId) {
                state.currentSessionId = null;
                state.messages = [];
                renderMessages();
            }
            await loadSessions();
            showToast('对话已删除', 'success');
        } catch (e) {
            console.error('Failed to delete session:', e);
            showToast('删除对话失败', 'error');
        }
    }

    async function clearChat() {
        if (!state.currentSessionId) return;
        if (!confirm('确定清空当前对话的所有消息吗？')) return;
        try {
            await API.post(`/api/chat/clear/${state.currentSessionId}`);
            state.messages = [];
            renderMessages();
            showToast('对话已清空', 'success');
        } catch (e) {
            console.error('Failed to clear chat:', e);
            showToast('清空对话失败', 'error');
        }
    }

    function renderSessionList() {
        const list = $('session-list');
        const search = $('session-search').value.toLowerCase();
        list.innerHTML = '';

        const filtered = state.sessions.filter(s =>
            !search || (s.title || '').toLowerCase().includes(search)
        );

        if (filtered.length === 0) {
            list.innerHTML = `
                <div class="empty-state">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5">
                        <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>
                    </svg>
                    <p>${search ? '没有匹配的对话' : '还没有对话，点击"新建对话"开始'}</p>
                </div>`;
            return;
        }

        for (const session of filtered) {
            const item = el('div', 'session-item');
            if (session.id === state.currentSessionId) {
                item.classList.add('active');
            }

            const iconSvg = '<svg class="session-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>';

            const info = el('div', 'session-info');
            const title = el('div', 'session-title', session.title || '新对话');
            const meta = el('div', 'session-meta');
            const msgCount = session.messageCount || 0;
            const date = session.createdAt ? formatDate(session.createdAt) : '';
            meta.textContent = `${msgCount} 条消息 · ${date}`;

            info.appendChild(title);
            info.appendChild(meta);

            const deleteBtn = el('button', 'session-delete');
            deleteBtn.title = '删除对话';
            deleteBtn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg>';
            deleteBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                deleteSession(session.id);
            });

            item.innerHTML = iconSvg;
            item.appendChild(info);
            item.appendChild(deleteBtn);

            item.addEventListener('click', () => switchSession(session.id));
            list.appendChild(item);
        }
    }

    function formatDate(dateStr) {
        const d = new Date(dateStr);
        const now = new Date();
        const diff = now - d;
        if (diff < 60000) return '刚刚';
        if (diff < 3600000) return `${Math.floor(diff / 60000)} 分钟前`;
        if (diff < 86400000) return `${Math.floor(diff / 3600000)} 小时前`;
        if (diff < 604800000) return `${Math.floor(diff / 86400000)} 天前`;
        return d.toLocaleDateString('zh-CN');
    }

    function updateChatHeader(title) {
        $('current-session-title').textContent = title || '新对话';
    }

    // ============================================
    //  Message Rendering
    // ============================================
    function renderMessages() {
        const list = $('messages-list');
        const welcome = $('welcome-screen');
        list.innerHTML = '';

        if (state.messages.length === 0 && !state.isStreaming) {
            welcome.style.display = 'flex';
        } else {
            welcome.style.display = 'none';
        }

        for (const msg of state.messages) {
            if (msg.role === 'system') continue;
            appendMessageElement(msg);
        }

        scrollToBottom();
    }

    function appendMessageElement(msg) {
        const list = $('messages-list');
        const messageDiv = el('div', `message ${msg.role}`);
        const welcome = $('welcome-screen');
        if (welcome.style.display !== 'none') {
            welcome.style.display = 'none';
        }

        // Header
        const header = el('div', 'message-header');
        const avatar = el('div', `message-avatar ${msg.role}`);

        if (msg.role === 'assistant') {
            avatar.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><path d="M12 2L2 7l10 5 10-5-10-5z"/><path d="M2 17l10 5 10-5"/><path d="M2 12l10 5 10-5"/></svg>';
        } else if (msg.role === 'tool') {
            avatar.classList.add('tool');
            avatar.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>';
        } else {
            avatar.textContent = 'U';
        }

        const roleLabel = msg.role === 'assistant' ? 'AI 助手' : (msg.role === 'tool' ? '工具调用' : (msg.name || '用户'));
        const roleSpan = el('span', 'message-role', roleLabel);

        header.appendChild(avatar);
        header.appendChild(roleSpan);
        messageDiv.appendChild(header);

        // Content
        const contentDiv = el('div', 'message-content');

        if (msg.role === 'tool') {
            // 检查是否为图片结果
            if (msg.content && msg.content.includes('"__image__"')) {
                try {
                    const parsed = JSON.parse(msg.content);
                    if (parsed.data) {
                        // 渲染为图片
                        const imageContainer = el('div', 'generated-image-container');
                        const img = document.createElement('img');
                        img.className = 'generated-image';
                        img.src = `data:image/png;base64,${parsed.data}`;
                        img.alt = `由 ${msg.name || 'AI'} 生成`;
                        img.style.maxWidth = '100%';
                        img.style.borderRadius = '8px';
                        img.style.cursor = 'pointer';
                        img.loading = 'lazy';
                        img.addEventListener('click', () => {
                            showImageModal(img.src);
                        });
                        imageContainer.appendChild(img);
                        contentDiv.appendChild(imageContainer);
                        messageDiv.appendChild(contentDiv);
                        list.appendChild(messageDiv);
                        return;
                    }
                } catch (e) { /* fall through to normal rendering */ }
            }

            // 渲染普通工具结果卡片
            const card = el('div', 'tool-call-card');
            const cardHeader = el('div', 'tool-call-header');
            const icon = el('div', 'tool-spinner tool-spinner-done');
            icon.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><polyline points="20 6 9 17 4 12"/></svg>';
            const nameSpan = el('span', 'tool-call-name', msg.name || 'unknown');
            const badge = el('span', 'tool-call-badge done-badge', '已完成');
            cardHeader.appendChild(icon);
            cardHeader.appendChild(nameSpan);
            cardHeader.appendChild(badge);
            card.appendChild(cardHeader);

            // 结果内容
            if (msg.content) {
                const resultDiv = el('div', 'tool-call-result');
                const label = el('div', 'label', '结果');
                const pre = document.createElement('pre');
                let resultText = msg.content;
                try {
                    const parsed = JSON.parse(resultText);
                    resultText = JSON.stringify(parsed, null, 2);
                } catch (e) { /* keep original */ }
                pre.textContent = resultText.length > 500 ? resultText.substring(0, 500) + '...(已截断)' : resultText;
                resultDiv.appendChild(label);
                resultDiv.appendChild(pre);
                card.appendChild(resultDiv);
            }

            cardHeader.addEventListener('click', () => {
                card.classList.toggle('expanded');
            });

            contentDiv.appendChild(card);
            messageDiv.appendChild(contentDiv);
            list.appendChild(messageDiv);
            return;
        }

        const bubble = el('div', 'message-bubble');

        if (msg.role === 'user') {
            const mdDiv = el('div', 'markdown-body');
            mdDiv.innerHTML = renderMarkdown(msg.content || '');
            bubble.appendChild(mdDiv);
        } else if (msg.role === 'assistant') {
            const mdDiv = el('div', 'markdown-body');
            mdDiv.innerHTML = renderMarkdown(msg.content || '');
            bubble.appendChild(mdDiv);
        }

        // Images
        if (msg.images && msg.images.length > 0) {
            const imgContainer = el('div', 'message-images');
            for (const img of msg.images) {
                const imgEl = document.createElement('img');
                imgEl.className = 'message-image';
                imgEl.src = img.startsWith('data:') ? img : `data:image/png;base64,${img}`;
                imgEl.addEventListener('click', () => openLightbox(imgEl.src));
                imgContainer.appendChild(imgEl);
            }
            bubble.appendChild(imgContainer);
        }

        contentDiv.appendChild(bubble);

        // Tool calls
        if (msg.toolCalls && msg.toolCalls.length > 0) {
            for (const tc of msg.toolCalls) {
                contentDiv.appendChild(createToolCallCard(tc));
            }
        }

        messageDiv.appendChild(contentDiv);
        list.appendChild(messageDiv);
    }

    function createToolCallCard(toolCall) {
        const card = el('div', 'tool-call-card');
        const header = el('div', 'tool-call-header');

        const icon = el('div', 'tool-call-icon');
        icon.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="9 18 15 12 9 6"/></svg>';

        const name = el('span', 'tool-call-name', toolCall.name || 'unknown_tool');
        const badge = el('span', 'tool-call-badge', 'Tool Call');

        header.appendChild(icon);
        header.appendChild(name);
        header.appendChild(badge);

        const details = el('div', 'tool-call-details');
        const label = el('div', 'label', '参数');
        const pre = document.createElement('pre');
        let argsText = toolCall.arguments || '';
        try {
            const parsed = JSON.parse(argsText);
            argsText = JSON.stringify(parsed, null, 2);
        } catch (e) { /* keep original */ }
        pre.textContent = argsText;
        details.appendChild(label);
        details.appendChild(pre);

        header.addEventListener('click', () => {
            card.classList.toggle('expanded');
        });

        card.appendChild(header);
        card.appendChild(details);
        return card;
    }

    // ============================================
    //  Image Lightbox
    // ============================================
    function openLightbox(src) {
        const lightbox = document.createElement('div');
        lightbox.id = 'image-lightbox';
        const img = document.createElement('img');
        img.src = src;
        lightbox.appendChild(img);
        lightbox.addEventListener('click', () => lightbox.remove());
        document.body.appendChild(lightbox);
    }

    // ============================================
    //  Chat - Send Message with SSE Streaming
    // ============================================
    async function sendMessage() {
        const input = $('message-input');
        const message = input.value.trim();
        if (!message && state.pendingImages.length === 0) return;
        if (state.isStreaming) return;
        if (!state.currentSessionId) {
            await createNewSession();
            if (!state.currentSessionId) return;
        }

        // Build user message
        const userMsg = {
            role: 'user',
            content: message,
            images: state.pendingImages.map(img => img.data),
        };

        // Add to state and render
        state.messages.push(userMsg);
        appendMessageElement(userMsg);

        // Clear input
        input.value = '';
        autoResizeTextarea();
        updateCharCount();
        clearImagePreviews();

        // Start streaming
        state.isStreaming = true;
        toggleSendStopButton(true);
        showTypingIndicator();

        try {
            const requestBody = {
                sessionId: state.currentSessionId,
                message: message,
                images: userMsg.images
            };

            state.abortController = new AbortController();

            const response = await fetch('/api/chat/send', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(requestBody),
                signal: state.abortController.signal
            });

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }

            // Process SSE stream
            await processSSEStream(response.body);

        } catch (e) {
            if (e.name === 'AbortError') {
                console.log('SSE stream aborted by user');
            } else {
                console.error('Send message error:', e);
                showToast(`发送失败: ${e.message}`, 'error');
            }
        } finally {
            state.isStreaming = false;
            state.abortController = null;
            toggleSendStopButton(false);
            removeTypingIndicator();
            // Reload sessions to update title/messageCount
            loadSessions();
        }
    }

    async function processSSEStream(body) {
        const reader = body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        state.currentAssistantContent = '';
        state.currentToolCalls = [];

        try {
            while (true) {
                const { done, value } = await reader.read();
                if (done) break;

                buffer += decoder.decode(value, { stream: true });
                const lines = buffer.split('\n');
                buffer = lines.pop() || '';

                for (const line of lines) {
                    if (!line.startsWith('data: ')) continue;
                    const jsonStr = line.substring(6).trim();
                    if (!jsonStr) continue;

                    try {
                        const data = JSON.parse(jsonStr);
                        handleSSEEvent(data);
                    } catch (e) {
                        console.error('SSE parse error:', e, jsonStr);
                    }
                }
            }

            // Process remaining buffer
            if (buffer.startsWith('data: ')) {
                const jsonStr = buffer.substring(6).trim();
                if (jsonStr) {
                    try {
                        const data = JSON.parse(jsonStr);
                        handleSSEEvent(data);
                    } catch (e) {
                        console.error('SSE parse error (final):', e);
                    }
                }
            }
        } finally {
            reader.releaseLock();
        }

        // Add completed assistant message to state
        if (state.currentAssistantContent || state.currentToolCalls.length > 0) {
            state.messages.push({
                role: 'assistant',
                content: state.currentAssistantContent,
                toolCalls: state.currentToolCalls.length > 0 ? state.currentToolCalls : undefined
            });
        }
        state.currentAssistantContent = '';
        state.currentToolCalls = [];
        state.currentAssistantMessageEl = null;
    }

    function handleSSEEvent(data) {
        switch (data.type) {
            case 'token': {
                removeTypingIndicator();
                // 如果有正在执行的工具，移除其加载状态
                removeToolLoadingIndicator();
                if (!state.currentAssistantMessageEl) {
                    state.currentAssistantMessageEl = createStreamingAssistantMessage();
                }
                state.currentAssistantContent += data.content || '';
                updateStreamingMessage();
                scrollToBottom();
                break;
            }
            case 'tool_start': {
                removeTypingIndicator();
                // 完成当前文本消息
                state.currentAssistantMessageEl = null;
                // 显示工具执行中状态
                showToolExecuting(data.name, data.arguments || '');
                const toolCall = { name: data.name, arguments: data.arguments || '' };
                state.currentToolCalls.push(toolCall);
                scrollToBottom();
                break;
            }
            case 'tool_result': {
                removeTypingIndicator();
                // 更新工具执行状态为完成
                updateToolResult(data.name, data.result || '');
                scrollToBottom();
                break;
            }
            case 'image': {
                removeTypingIndicator();
                removeToolLoadingIndicator();
                // 在对话中显示生成的图片
                appendImageToStream(data.data, data.name || 'generate_image');
                scrollToBottom();
                break;
            }
            case 'tool_call': {
                removeTypingIndicator();
                if (state.currentAssistantContent && state.currentAssistantMessageEl) {
                    state.currentAssistantMessageEl = null;
                }
                const toolCall = {
                    name: data.name,
                    arguments: data.arguments || ''
                };
                state.currentToolCalls.push(toolCall);
                appendToolCallToStream(toolCall);
                scrollToBottom();
                break;
            }
            case 'complete': {
                removeToolLoadingIndicator();
                if (state.currentAssistantContent && !state.currentAssistantMessageEl) {
                    // Already finalized
                } else if (state.currentAssistantContent) {
                    state.currentAssistantMessageEl = null;
                }
                break;
            }
            case 'error': {
                removeTypingIndicator();
                removeToolLoadingIndicator();
                showToast(`错误: ${data.content || '未知错误'}`, 'error');
                if (state.currentAssistantContent) {
                    state.currentAssistantMessageEl = null;
                }
                break;
            }
        }
    }

    function showTypingIndicator() {
        const list = $('messages-list');
        const welcome = $('welcome-screen');
        if (welcome.style.display !== 'none') {
            welcome.style.display = 'none';
        }

        const msg = el('div', 'message assistant');
        msg.id = 'typing-indicator-msg';

        const header = el('div', 'message-header');
        const avatar = el('div', 'message-avatar assistant');
        avatar.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><path d="M12 2L2 7l10 5 10-5-10-5z"/><path d="M2 17l10 5 10-5"/><path d="M2 12l10 5 10-5"/></svg>';
        const roleSpan = el('span', 'message-role', 'AI 助手');
        header.appendChild(avatar);
        header.appendChild(roleSpan);
        msg.appendChild(header);

        const content = el('div', 'message-content');
        const bubble = el('div', 'message-bubble');
        const indicator = el('div', 'typing-indicator');
        indicator.innerHTML = '<span></span><span></span><span></span>';
        bubble.appendChild(indicator);
        content.appendChild(bubble);
        msg.appendChild(content);

        list.appendChild(msg);
        scrollToBottom();
    }

    function removeTypingIndicator() {
        const indicator = $('typing-indicator-msg');
        if (indicator) indicator.remove();
    }

    function createStreamingAssistantMessage() {
        const list = $('messages-list');
        const msg = el('div', 'message assistant');

        const header = el('div', 'message-header');
        const avatar = el('div', 'message-avatar assistant');
        avatar.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><path d="M12 2L2 7l10 5 10-5-10-5z"/><path d="M2 17l10 5 10-5"/><path d="M2 12l10 5 10-5"/></svg>';
        const roleSpan = el('span', 'message-role', 'AI 助手');
        header.appendChild(avatar);
        header.appendChild(roleSpan);
        msg.appendChild(header);

        const contentDiv = el('div', 'message-content');
        const bubble = el('div', 'message-bubble');
        const mdDiv = el('div', 'markdown-body');
        bubble.appendChild(mdDiv);
        contentDiv.appendChild(bubble);
        msg.appendChild(contentDiv);

        list.appendChild(msg);
        return { msg, bubble, mdDiv };
    }

    function updateStreamingMessage() {
        if (!state.currentAssistantMessageEl) return;
        state.currentAssistantMessageEl.mdDiv.innerHTML = renderMarkdown(state.currentAssistantContent);
    }

    function appendToolCallToStream(toolCall) {
        const list = $('messages-list');
        const msg = el('div', 'message assistant');

        const header = el('div', 'message-header');
        const avatar = el('div', 'message-avatar tool');
        avatar.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>';
        const roleSpan = el('span', 'message-role', '工具调用');
        header.appendChild(avatar);
        header.appendChild(roleSpan);
        msg.appendChild(header);

        const contentDiv = el('div', 'message-content');
        contentDiv.appendChild(createToolCallCard(toolCall));
        msg.appendChild(contentDiv);

        list.appendChild(msg);
    }

    // 显示工具执行中状态（带加载动画）
    function showToolExecuting(toolName, args) {
        const list = $('messages-list');
        const msg = el('div', 'message assistant');
        msg.id = 'tool-executing-msg';

        const header = el('div', 'message-header');
        const avatar = el('div', 'message-avatar tool');
        avatar.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>';
        const roleSpan = el('span', 'message-role', '工具调用');
        header.appendChild(avatar);
        header.appendChild(roleSpan);
        msg.appendChild(header);

        const contentDiv = el('div', 'message-content');
        const card = el('div', 'tool-call-card executing');
        card.id = 'tool-executing-card';

        const cardHeader = el('div', 'tool-call-header');
        const spinner = el('div', 'tool-spinner');

        const nameSpan = el('span', 'tool-call-name', toolName);
        const badge = el('span', 'tool-call-badge executing-badge', '执行中...');

        cardHeader.appendChild(spinner);
        cardHeader.appendChild(nameSpan);
        cardHeader.appendChild(badge);

        // 参数区域
        let detailsHtml = '';
        if (args && args.trim()) {
            let argsDisplay = args;
            try {
                const parsed = JSON.parse(args);
                argsDisplay = JSON.stringify(parsed, null, 2);
            } catch (e) { /* keep original */ }
            detailsHtml = `<div class="tool-call-details" style="display:block">
                <div class="label">参数</div>
                <pre>${escapeHtml(argsDisplay)}</pre>
            </div>`;
        }

        card.appendChild(cardHeader);
        if (detailsHtml) {
            const detailsContainer = el('div');
            detailsContainer.innerHTML = detailsHtml;
            card.appendChild(detailsContainer.firstChild);
        }

        // 结果占位区域
        const resultDiv = el('div', 'tool-call-result');
        resultDiv.id = 'tool-result-area';
        card.appendChild(resultDiv);

        contentDiv.appendChild(card);
        msg.appendChild(contentDiv);
        list.appendChild(msg);
    }

    // 更新工具执行结果
    function updateToolResult(toolName, result) {
        const card = $('tool-executing-card');
        if (!card) return;

        // 更新状态徽章
        card.classList.remove('executing');
        const badge = card.querySelector('.executing-badge');
        if (badge) {
            badge.textContent = '已完成';
            badge.classList.remove('executing-badge');
            badge.classList.add('done-badge');
        }

        // 移除加载动画
        const spinner = card.querySelector('.tool-spinner');
        if (spinner) {
            spinner.classList.add('tool-spinner-done');
            spinner.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><polyline points="20 6 9 17 4 12"/></svg>';
        }

        // 显示结果
        const resultArea = $('tool-result-area');
        if (resultArea && result) {
            resultArea.innerHTML = `<div class="label">结果</div><pre>${escapeHtml(result)}</pre>`;
        }

        // 移除 id 以便后续工具调用可以创建新的
        const executingMsg = $('tool-executing-msg');
        if (executingMsg) executingMsg.id = '';
        card.id = '';
        resultArea?.removeAttribute('id');
    }

    // 移除工具加载指示器（如果没有收到 result 就收到新 token）
    function removeToolLoadingIndicator() {
        const executingMsg = $('tool-executing-msg');
        if (executingMsg) {
            // 保留卡片但标记为完成
            const card = $('tool-executing-card');
            if (card) {
                card.classList.remove('executing');
                const badge = card.querySelector('.executing-badge');
                if (badge) {
                    badge.textContent = '已完成';
                    badge.classList.remove('executing-badge');
                    badge.classList.add('done-badge');
                }
                const spinner = card.querySelector('.tool-spinner');
                if (spinner) {
                    spinner.classList.add('tool-spinner-done');
                    spinner.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><polyline points="20 6 9 17 4 12"/></svg>';
                }
                card.id = '';
            }
            executingMsg.id = '';
        }
    }

    // 在对话中显示生成的图片
    function appendImageToStream(base64Data, toolName) {
        const list = $('messages-list');
        const msg = el('div', 'message assistant');

        const header = el('div', 'message-header');
        const avatar = el('div', 'message-avatar');
        avatar.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:16px;height:16px"><rect x="3" y="3" width="18" height="18" rx="2" ry="2"/><circle cx="8.5" cy="8.5" r="1.5"/><polyline points="21 15 16 10 5 21"/></svg>';
        const roleSpan = el('span', 'message-role', 'AI');
        header.appendChild(avatar);
        header.appendChild(roleSpan);
        msg.appendChild(header);

        const contentDiv = el('div', 'message-content');

        // 创建图片容器
        const imageContainer = el('div', 'generated-image-container');

        const img = document.createElement('img');
        img.className = 'generated-image';
        img.src = `data:image/png;base64,${base64Data}`;
        img.alt = `由 ${toolName} 生成`;
        img.style.maxWidth = '100%';
        img.style.borderRadius = '8px';
        img.style.cursor = 'pointer';
        img.loading = 'lazy';

        // 点击放大查看
        img.addEventListener('click', () => {
            showImageModal(img.src);
        });

        imageContainer.appendChild(img);
        contentDiv.appendChild(imageContainer);
        msg.appendChild(contentDiv);
        list.appendChild(msg);
    }

    // 图片放大查看弹窗
    function showImageModal(src) {
        const modal = document.createElement('div');
        modal.className = 'image-modal';
        modal.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.8);display:flex;align-items:center;justify-content:center;z-index:9999;cursor:zoom-out;';

        const modalImg = document.createElement('img');
        modalImg.src = src;
        modalImg.style.cssText = 'max-width:90%;max-height:90%;border-radius:8px;box-shadow:0 8px 32px rgba(0,0,0,0.5);';

        modal.appendChild(modalImg);
        modal.addEventListener('click', () => modal.remove());
        document.body.appendChild(modal);
    }

    function toggleSendStopButton(isStreaming) {
        $('send-btn').style.display = isStreaming ? 'none' : 'flex';
        $('stop-btn').style.display = isStreaming ? 'flex' : 'none';
    }

    async function stopGeneration() {
        try {
            if (state.abortController) {
                state.abortController.abort();
            }
            if (state.currentSessionId) {
                await API.post(`/api/chat/stop/${state.currentSessionId}`);
            }
        } catch (e) {
            console.error('Stop generation error:', e);
        }
        state.isStreaming = false;
        toggleSendStopButton(false);
        removeTypingIndicator();
    }

    // ============================================
    //  Auto-scroll
    // ============================================
    function scrollToBottom() {
        const container = $('messages-container');
        container.scrollTop = container.scrollHeight;
    }

    // ============================================
    //  Textarea Auto-Resize
    // ============================================
    function autoResizeTextarea() {
        const textarea = $('message-input');
        textarea.style.height = 'auto';
        textarea.style.height = Math.min(textarea.scrollHeight, 200) + 'px';
    }

    function updateCharCount() {
        const input = $('message-input');
        $('char-count').textContent = input.value.length;
    }

    // ============================================
    //  Image Upload
    // ============================================
    function handleImageFiles(files) {
        for (const file of files) {
            if (!file.type.startsWith('image/')) continue;
            if (file.size > 10 * 1024 * 1024) {
                showToast(`图片 ${file.name} 超过 10MB 限制`, 'error');
                continue;
            }

            const reader = new FileReader();
            reader.onload = (e) => {
                const dataUrl = e.target.result;
                const base64 = dataUrl.split(',')[1];
                state.pendingImages.push({
                    data: base64,
                    dataUrl: dataUrl,
                    name: file.name
                });
                renderImagePreviews();
            };
            reader.readAsDataURL(file);
        }
    }

    function renderImagePreviews() {
        const container = $('image-preview-container');
        container.innerHTML = '';

        for (let i = 0; i < state.pendingImages.length; i++) {
            const img = state.pendingImages[i];
            const preview = el('div', 'image-preview');
            const imgEl = document.createElement('img');
            imgEl.src = img.dataUrl;
            const removeBtn = el('button', 'remove-image', '×');
            removeBtn.addEventListener('click', () => {
                state.pendingImages.splice(i, 1);
                renderImagePreviews();
            });
            preview.appendChild(imgEl);
            preview.appendChild(removeBtn);
            container.appendChild(preview);
        }
    }

    function clearImagePreviews() {
        state.pendingImages = [];
        renderImagePreviews();
    }

    // ============================================
    //  Settings Management
    // ============================================
    async function loadSettings() {
        try {
            state.settings = await API.get('/api/settings');
            populateSettingsForm(state.settings);
        } catch (e) {
            console.error('Failed to load settings:', e);
            showToast('加载设置失败', 'error');
        }
    }

    function populateSettingsForm(settings) {
        if (!settings) return;
        const model = settings.model || {};
        $('model-provider').value = model.provider || 'openai';
        $('model-base-url').value = model.baseUrl || '';
        $('model-api-key').value = model.apiKey || '';
        $('model-name').value = model.modelName || '';
        $('model-temperature').value = model.temperature ?? 0.7;
        $('temperature-value').textContent = model.temperature ?? 0.7;
        $('model-max-tokens').value = model.maxTokens || 4096;
        $('model-timeout').value = model.timeoutSeconds || 120;
        $('model-enable-vision').checked = model.enableVision ?? true;
        $('system-prompt').value = settings.systemPrompt || '';
    }

    async function saveSettings() {
        const settings = {
            model: {
                provider: $('model-provider').value,
                baseUrl: $('model-base-url').value,
                apiKey: $('model-api-key').value,
                modelName: $('model-name').value,
                temperature: parseFloat($('model-temperature').value),
                maxTokens: parseInt($('model-max-tokens').value),
                enableVision: $('model-enable-vision').checked,
                timeoutSeconds: parseInt($('model-timeout').value)
            },
            systemPrompt: $('system-prompt').value,
            mcpServers: state.mcpServers || [],
            skills: state.skills || []
        };

        try {
            await API.put('/api/settings', settings);
            state.settings = settings;
            showToast('设置已保存', 'success');
        } catch (e) {
            console.error('Failed to save settings:', e);
            showToast('保存设置失败', 'error');
        }
    }

    function openSettings() {
        loadSettings();
        loadMCPServers();
        loadSkills();
        $('settings-modal').style.display = 'flex';
    }

    function closeSettings() {
        $('settings-modal').style.display = 'none';
    }

    // ============================================
    //  MCP Server Management
    // ============================================
    async function loadMCPServers() {
        try {
            state.mcpServers = await API.get('/api/mcp/servers');
            renderMCPServers();
        } catch (e) {
            console.error('Failed to load MCP servers:', e);
        }
    }

    function renderMCPServers() {
        const list = $('mcp-servers-list');
        list.innerHTML = '';

        if (!state.mcpServers || state.mcpServers.length === 0) {
            list.innerHTML = `
                <div class="empty-state">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5">
                        <rect x="2" y="2" width="20" height="8" rx="2" ry="2"/>
                        <rect x="2" y="14" width="20" height="8" rx="2" ry="2"/>
                        <line x1="6" y1="6" x2="6.01" y2="6"/>
                        <line x1="6" y1="18" x2="6.01" y2="18"/>
                    </svg>
                    <p>还没有配置 MCP 服务</p>
                </div>`;
            return;
        }

        for (const server of state.mcpServers) {
            const card = el('div', 'item-card');

            const header = el('div', 'item-header');

            const dot = el('div', 'status-dot');
            if (!server.isEnabled) {
                dot.classList.add('disabled');
            } else if (server.isConnected) {
                dot.classList.add('connected');
            } else {
                dot.classList.add('disconnected');
            }

            const name = el('div', 'item-name', server.name);
            const badge = el('span', `badge ${server.isConnected ? 'badge-success' : (server.isEnabled ? 'badge-warning' : 'badge-muted')}`);
            badge.textContent = server.isConnected ? '已连接' : (server.isEnabled ? '未连接' : '已禁用');

            const actions = el('div', 'item-actions');

            // Toggle switch (enable/disable)
            const toggleLabel = el('label', 'toggle-switch');
            const toggleInput = document.createElement('input');
            toggleInput.type = 'checkbox';
            toggleInput.checked = server.isEnabled;
            toggleInput.addEventListener('change', () => toggleMCPServer(server.name));
            const toggleSlider = el('span', 'toggle-slider');
            toggleLabel.appendChild(toggleInput);
            toggleLabel.appendChild(toggleSlider);

            // Start button
            const startBtn = el('button', 'btn-small', '启动');
            startBtn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="5 3 19 12 5 21 5 3"/></svg> 启动';
            startBtn.addEventListener('click', () => startMCPServer(server.name));

            // Stop button
            const stopBtn = el('button', 'btn-small', '停止');
            stopBtn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="6" y="4" width="4" height="16"/><rect x="14" y="4" width="4" height="16"/></svg> 停止';
            stopBtn.addEventListener('click', () => stopMCPServer(server.name));

            // Tools button
            const toolsBtn = el('button', 'btn-small', '工具');
            toolsBtn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg> 工具';
            toolsBtn.addEventListener('click', () => viewMCPServerTools(server.name));

            // Delete button
            const deleteBtn = el('button', 'btn-danger', '删除');
            deleteBtn.addEventListener('click', () => deleteMCPServer(server.name));

            actions.appendChild(toggleLabel);
            actions.appendChild(startBtn);
            actions.appendChild(stopBtn);
            actions.appendChild(toolsBtn);
            actions.appendChild(deleteBtn);

            header.appendChild(dot);
            header.appendChild(name);
            header.appendChild(badge);
            header.appendChild(actions);

            card.appendChild(header);

            // Meta info
            const meta = el('div', 'item-meta');
            const transport = el('span');
            transport.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:12px;height:12px"><path d="M5 18H3c-.6 0-1-.4-1-1V7c0-.6.4-1 1-1h2c.6 0 1 .4 1 1v10c0 .6-.4 1-1 1z"/><path d="M19 18h-2c-.6 0-1-.4-1-1V7c0-.6.4-1 1-1h2c.6 0 1 .4 1 1v10c0 .6-.4 1-1 1z"/><path d="M12 18h-2c-.6 0-1-.4-1-1V7c0-.6.4-1 1-1h2c.6 0 1 .4 1 1v10c0 .6-.4 1-1 1z"/></svg>';
            transport.innerHTML += ` ${server.transport || 'stdio'}`;
            meta.appendChild(transport);

            if (server.toolCount !== undefined && server.toolCount !== null) {
                const tools = el('span');
                tools.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:12px;height:12px"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg> ${server.toolCount} 工具`;
                meta.appendChild(tools);
            }

            card.appendChild(meta);
            list.appendChild(card);
        }
    }

    async function addMCPServer() {
        const name = $('mcp-name').value.trim();
        if (!name) {
            showToast('请输入服务名称', 'error');
            return;
        }

        const transport = $('mcp-transport').value;
        const config = {
            name: name,
            transport: transport,
            command: $('mcp-command').value.trim() || null,
            args: $('mcp-args').value.trim() ? $('mcp-args').value.trim().split(/\s+/) : [],
            env: {},
            url: $('mcp-url').value.trim() || null,
            headers: {},
            enabled: $('mcp-enabled').checked,
            autoStart: $('mcp-autostart').checked
        };

        // Parse env vars
        const envStr = $('mcp-env').value.trim();
        if (envStr) {
            const pairs = envStr.split(',');
            for (const pair of pairs) {
                const [key, ...valParts] = pair.split('=');
                if (key && valParts.length > 0) {
                    config.env[key.trim()] = valParts.join('=').trim();
                }
            }
        }

        try {
            await API.post('/api/mcp/servers', config);
            showToast('MCP 服务已添加', 'success');
            // Clear form
            $('mcp-name').value = '';
            $('mcp-command').value = '';
            $('mcp-args').value = '';
            $('mcp-url').value = '';
            $('mcp-env').value = '';
            await loadMCPServers();
        } catch (e) {
            console.error('Failed to add MCP server:', e);
            showToast('添加 MCP 服务失败', 'error');
        }
    }

    async function deleteMCPServer(name) {
        if (!confirm(`确定删除 MCP 服务 "${name}" 吗？`)) return;
        try {
            await API.del(`/api/mcp/servers/${name}`);
            showToast('MCP 服务已删除', 'success');
            await loadMCPServers();
        } catch (e) {
            console.error('Failed to delete MCP server:', e);
            showToast('删除 MCP 服务失败', 'error');
        }
    }

    async function toggleMCPServer(name) {
        try {
            await API.put(`/api/mcp/servers/${name}/toggle`);
            await loadMCPServers();
        } catch (e) {
            console.error('Failed to toggle MCP server:', e);
            showToast('切换 MCP 服务状态失败', 'error');
            await loadMCPServers();
        }
    }

    async function startMCPServer(name) {
        try {
            const result = await API.post(`/api/mcp/servers/${name}/start`);
            if (result.success) {
                showToast(`MCP 服务 "${name}" 已启动`, 'success');
            } else {
                showToast(`启动失败: ${result.message || '未知错误'}`, 'error');
            }
            await loadMCPServers();
        } catch (e) {
            console.error('Failed to start MCP server:', e);
            showToast('启动 MCP 服务失败', 'error');
        }
    }

    async function stopMCPServer(name) {
        try {
            await API.post(`/api/mcp/servers/${name}/stop`);
            showToast(`MCP 服务 "${name}" 已停止`, 'info');
            await loadMCPServers();
        } catch (e) {
            console.error('Failed to stop MCP server:', e);
            showToast('停止 MCP 服务失败', 'error');
        }
    }

    async function checkMCPHealth() {
        try {
            const health = await API.get('/api/mcp/health');
            let connected = 0;
            let total = 0;
            for (const [name, status] of Object.entries(health)) {
                total++;
                if (status) connected++;
            }
            showToast(`健康检查完成: ${connected}/${total} 服务在线`, 'info');
            await loadMCPServers();
        } catch (e) {
            console.error('Health check error:', e);
            showToast('健康检查失败', 'error');
        }
    }

    async function viewMCPServerTools(serverName) {
        try {
            const tools = await API.get('/api/mcp/tools');
            const filtered = tools.filter(t => t.serverName === serverName);
            renderToolsModal(filtered, `${serverName} - 可用工具`);
        } catch (e) {
            console.error('Failed to load tools:', e);
            showToast('加载工具列表失败', 'error');
        }
    }

    function renderToolsModal(tools, title) {
        $('tools-modal-title').textContent = title || '可用工具';
        const list = $('tools-list');
        list.innerHTML = '';

        if (!tools || tools.length === 0) {
            list.innerHTML = '<div class="empty-state"><p>没有可用的工具</p></div>';
        } else {
            for (const tool of tools) {
                const item = el('div', 'tool-item');
                const header = el('div', 'tool-item-header');
                const name = el('span', 'tool-item-name', tool.name);
                const server = el('span', 'tool-item-server', tool.serverName || '');
                header.appendChild(name);
                header.appendChild(server);

                const desc = el('div', 'tool-item-description', tool.description || '');

                item.appendChild(header);
                item.appendChild(desc);

                if (tool.inputSchema) {
                    let schemaStr = tool.inputSchema;
                    try {
                        if (typeof schemaStr === 'object') {
                            schemaStr = JSON.stringify(schemaStr, null, 2);
                        } else {
                            schemaStr = JSON.stringify(JSON.parse(schemaStr), null, 2);
                        }
                    } catch (e) { /* keep original */ }
                    const schema = el('div', 'tool-item-schema');
                    schema.textContent = schemaStr;
                    item.appendChild(schema);
                }

                list.appendChild(item);
            }
        }

        $('tools-modal').style.display = 'flex';
    }

    // ============================================
    //  Skill Management
    // ============================================
    async function loadSkills() {
        try {
            const data = await API.get('/api/skills');
            state.skills = data.map(item => item.config || item);
            renderSkills();
        } catch (e) {
            console.error('Failed to load skills:', e);
        }
    }

    function renderSkills() {
        const list = $('skills-list');
        list.innerHTML = '';

        if (!state.skills || state.skills.length === 0) {
            list.innerHTML = `
                <div class="empty-state">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5">
                        <polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/>
                    </svg>
                    <p>还没有配置技能</p>
                </div>`;
            return;
        }

        for (const skill of state.skills) {
            const card = el('div', 'item-card');
            const header = el('div', 'item-header');

            const dot = el('div', 'status-dot');
            dot.classList.add(skill.enabled ? 'ready' : 'disabled');

            const name = el('div', 'item-name', skill.name);
            const badge = el('span', `badge ${skill.enabled ? 'badge-success' : 'badge-muted'}`);
            badge.textContent = skill.enabled ? '就绪' : '已禁用';

            const actions = el('div', 'item-actions');

            // Toggle switch (enable/disable)
            const toggleLabel = el('label', 'toggle-switch');
            const toggleInput = document.createElement('input');
            toggleInput.type = 'checkbox';
            toggleInput.checked = skill.enabled;
            toggleInput.addEventListener('change', () => toggleSkill(skill.name));
            const toggleSlider = el('span', 'toggle-slider');
            toggleLabel.appendChild(toggleInput);
            toggleLabel.appendChild(toggleSlider);

            const deleteBtn = el('button', 'btn-danger', '删除');
            deleteBtn.addEventListener('click', () => deleteSkill(skill.name));
            actions.appendChild(toggleLabel);
            actions.appendChild(deleteBtn);

            header.appendChild(dot);
            header.appendChild(name);
            header.appendChild(badge);
            header.appendChild(actions);
            card.appendChild(header);

            if (skill.description) {
                const desc = el('div', 'item-description', skill.description);
                card.appendChild(desc);
            }

            const meta = el('div', 'item-meta');
            const type = el('span');
            type.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:12px;height:12px"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg> 类型: ${skill.type || 'mcp'}`;
            meta.appendChild(type);

            if (skill.mcpServer) {
                const server = el('span');
                server.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:12px;height:12px"><rect x="2" y="2" width="20" height="8" rx="2" ry="2"/><rect x="2" y="14" width="20" height="8" rx="2" ry="2"/></svg> 服务: ${skill.mcpServer}`;
                meta.appendChild(server);
            }

            if (skill.mcpTool) {
                const tool = el('span');
                tool.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="width:12px;height:12px"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg> 工具: ${skill.mcpTool}`;
                meta.appendChild(tool);
            }

            card.appendChild(meta);
            list.appendChild(card);
        }
    }

    async function addSkill() {
        const name = $('skill-name').value.trim();
        if (!name) {
            showToast('请输入技能名称', 'error');
            return;
        }

        const skill = {
            name: name,
            description: $('skill-description').value.trim(),
            type: $('skill-type').value,
            mcpServer: $('skill-mcp-server').value.trim() || null,
            mcpTool: $('skill-mcp-tool').value.trim() || null,
            scriptPath: $('skill-script').value.trim() || null,
            enabled: $('skill-enabled').checked
        };

        try {
            await API.post('/api/skills', skill);
            showToast('技能已添加', 'success');
            // Clear form
            $('skill-name').value = '';
            $('skill-description').value = '';
            $('skill-mcp-server').value = '';
            $('skill-mcp-tool').value = '';
            $('skill-script').value = '';
            await loadSkills();
        } catch (e) {
            console.error('Failed to add skill:', e);
            showToast('添加技能失败', 'error');
        }
    }

    async function deleteSkill(name) {
        if (!confirm(`确定删除技能 "${name}" 吗？`)) return;
        try {
            await API.del(`/api/skills/${name}`);
            showToast('技能已删除', 'success');
            await loadSkills();
        } catch (e) {
            console.error('Failed to delete skill:', e);
            showToast('删除技能失败', 'error');
        }
    }

    async function toggleSkill(name) {
        try {
            await API.put(`/api/skills/${name}/toggle`);
            await loadSkills();
        } catch (e) {
            console.error('Failed to toggle skill:', e);
            showToast('切换技能状态失败', 'error');
            await loadSkills();
        }
    }

    async function reloadSkills() {
        try {
            await API.post('/api/skills/reload');
            showToast('技能已重新加载', 'success');
            await loadSkills();
        } catch (e) {
            console.error('Failed to reload skills:', e);
            showToast('重新加载技能失败', 'error');
        }
    }

    // ============================================
    //  Theme Toggle
    // ============================================
    function toggleTheme() {
        const html = document.documentElement;
        const current = html.getAttribute('data-theme');
        const newTheme = current === 'dark' ? 'light' : 'dark';
        html.setAttribute('data-theme', newTheme);
        localStorage.setItem('theme', newTheme);

        const moonIcon = document.querySelector('.icon-moon');
        const sunIcon = document.querySelector('.icon-sun');
        if (newTheme === 'light') {
            moonIcon.style.display = 'none';
            sunIcon.style.display = 'block';
        } else {
            moonIcon.style.display = 'block';
            sunIcon.style.display = 'none';
        }
    }

    function loadTheme() {
        const saved = localStorage.getItem('theme') || 'dark';
        document.documentElement.setAttribute('data-theme', saved);
        const moonIcon = document.querySelector('.icon-moon');
        const sunIcon = document.querySelector('.icon-sun');
        if (saved === 'light') {
            moonIcon.style.display = 'none';
            sunIcon.style.display = 'block';
        } else {
            moonIcon.style.display = 'block';
            sunIcon.style.display = 'none';
        }
    }

    // ============================================
    //  Drag and Drop
    // ============================================
    function setupDragAndDrop() {
        const inputWrapper = document.querySelector('.input-wrapper');
        const inputArea = $('input-area');

        ['dragenter', 'dragover'].forEach(eventName => {
            inputArea.addEventListener(eventName, (e) => {
                e.preventDefault();
                e.stopPropagation();
                inputWrapper.classList.add('drag-over');
            });
        });

        ['dragleave', 'drop'].forEach(eventName => {
            inputArea.addEventListener(eventName, (e) => {
                e.preventDefault();
                e.stopPropagation();
                inputWrapper.classList.remove('drag-over');
            });
        });

        inputArea.addEventListener('drop', (e) => {
            const files = e.dataTransfer.files;
            handleImageFiles(files);
        });
    }

    // ============================================
    //  Event Listeners Setup
    // ============================================
    function setupEventListeners() {
        // New chat button
        $('new-chat-btn').addEventListener('click', createNewSession);

        // Session search
        $('session-search').addEventListener('input', renderSessionList);

        // Settings button
        $('settings-btn').addEventListener('click', openSettings);
        $('close-settings-btn').addEventListener('click', closeSettings);
        $('settings-close-btn').addEventListener('click', closeSettings);

        // Settings modal - click outside to close
        $('settings-modal').addEventListener('click', (e) => {
            if (e.target === $('settings-modal')) closeSettings();
        });

        // Settings tabs
        document.querySelectorAll('.settings-tab').forEach(tab => {
            tab.addEventListener('click', () => {
                document.querySelectorAll('.settings-tab').forEach(t => t.classList.remove('active'));
                document.querySelectorAll('.settings-tab-content').forEach(c => c.classList.remove('active'));
                tab.classList.add('active');
                $(`tab-${tab.dataset.tab}`).classList.add('active');
            });
        });

        // Temperature slider
        $('model-temperature').addEventListener('input', (e) => {
            $('temperature-value').textContent = parseFloat(e.target.value).toFixed(1);
        });

        // Save model settings
        $('save-model-btn').addEventListener('click', saveSettings);

        // MCP transport toggle
        $('mcp-transport').addEventListener('change', (e) => {
            const isSse = e.target.value === 'sse';
            $('mcp-command-group').style.display = isSse ? 'none' : 'block';
            $('mcp-args-group').style.display = isSse ? 'none' : 'block';
            $('mcp-url-group').style.display = isSse ? 'block' : 'none';
        });

        // MCP buttons
        $('add-mcp-btn').addEventListener('click', addMCPServer);
        $('mcp-health-btn').addEventListener('click', checkMCPHealth);
        $('mcp-refresh-btn').addEventListener('click', loadMCPServers);

        // Skills type toggle
        $('skill-type').addEventListener('change', (e) => {
            const isScript = e.target.value === 'script';
            $('skill-mcp-server-group').style.display = isScript ? 'none' : 'block';
            $('skill-mcp-tool-group').style.display = isScript ? 'none' : 'block';
            $('skill-script-group').style.display = isScript ? 'block' : 'none';
        });

        // Skills buttons
        $('add-skill-btn').addEventListener('click', addSkill);
        $('skills-reload-btn').addEventListener('click', reloadSkills);

        // Tools modal close
        $('close-tools-btn').addEventListener('click', () => {
            $('tools-modal').style.display = 'none';
        });
        $('tools-modal').addEventListener('click', (e) => {
            if (e.target === $('tools-modal')) {
                $('tools-modal').style.display = 'none';
            }
        });

        // Theme toggle
        $('theme-toggle-btn').addEventListener('click', toggleTheme);

        // Clear chat
        $('clear-chat-btn').addEventListener('click', clearChat);

        // Send / Stop buttons
        $('send-btn').addEventListener('click', sendMessage);
        $('stop-btn').addEventListener('click', stopGeneration);

        // Message input
        const input = $('message-input');
        input.addEventListener('input', () => {
            autoResizeTextarea();
            updateCharCount();
        });
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                sendMessage();
            }
        });

        // Image upload
        $('image-upload-btn').addEventListener('click', () => {
            $('image-input').click();
        });
        $('image-input').addEventListener('change', (e) => {
            handleImageFiles(e.target.files);
            e.target.value = '';
        });

        // Sidebar toggle (mobile)
        $('sidebar-toggle').addEventListener('click', () => {
            $('sidebar').classList.toggle('open');
        });

        // Suggestion cards
        document.querySelectorAll('.suggestion-card').forEach(card => {
            card.addEventListener('click', () => {
                const prompt = card.dataset.prompt;
                if (prompt) {
                    input.value = prompt;
                    autoResizeTextarea();
                    updateCharCount();
                    input.focus();
                }
            });
        });

        // Close sidebar on mobile when clicking outside
        document.addEventListener('click', (e) => {
            const sidebar = $('sidebar');
            const toggle = $('sidebar-toggle');
            if (window.innerWidth <= 768 &&
                sidebar.classList.contains('open') &&
                !sidebar.contains(e.target) &&
                !toggle.contains(e.target)) {
                sidebar.classList.remove('open');
            }
        });

        // Drag and drop
        setupDragAndDrop();
    }

    // ============================================
    //  Initialize
    // ============================================
    async function init() {
        loadTheme();
        setupEventListeners();

        try {
            await loadSessions();
            if (state.sessions.length === 0) {
                await createNewSession();
            } else {
                await switchSession(state.sessions[0].id);
            }
        } catch (e) {
            console.error('Initialization error:', e);
            showToast('初始化失败，请检查后端服务', 'error');
        }
    }

    // Start when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
