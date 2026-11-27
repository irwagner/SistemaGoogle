(() => {
  'use strict';

  const node = (tag, text) => {
    const item = document.createElement(tag);
    if (text != null) item.textContent = text;
    return item;
  };

  function inline(parent, text) {
    const pattern = /`([^`\n]+)`|\*\*([^*\n]+)\*\*|__([^_\n]+)__|\*([^*\n]+)\*|\r?\n/g;
    let offset = 0;
    for (const match of text.matchAll(pattern)) {
      parent.append(text.slice(offset, match.index));
      if (match[1]) parent.append(node('code', match[1]));
      else if (match[2] || match[3]) parent.append(node('strong', match[2] || match[3]));
      else if (match[4]) parent.append(node('em', match[4]));
      else parent.append(node('br'));
      offset = match.index + match[0].length;
    }
    parent.append(text.slice(offset));
  }

  function format(text) {
    const output = document.createDocumentFragment();
    const lines = text.replace(/\r\n?/g, '\n').split('\n');
    let paragraph = [];
    let list = null;
    let pendingCode = null;

    function flush() {
      if (paragraph.length === 0) return;
      const block = node('p');
      inline(block, paragraph.join('\n'));
      output.append(block);
      paragraph = [];
    }
    function finishCode() {
      const pre = node('pre');
      pre.append(node('code', pendingCode.lines.join('\n')));
      if (pendingCode.language) pre.dataset.language = pendingCode.language;
      output.append(pre);
      pendingCode = null;
    }

    for (const line of lines) {
      const fence = /^\s{0,3}(`{3,}|~{3,})([^`~]*)$/.exec(line);
      if (pendingCode) {
        if (fence && fence[1][0] === pendingCode.mark[0] && fence[1].length >= pendingCode.mark.length && !fence[2].trim()) finishCode();
        else pendingCode.lines.push(line);
        continue;
      }
      const heading = /^\s{0,3}(#{1,6})\s+(.+)$/.exec(line);
      const bullet = /^\s{0,3}([-+*]|\d+[.)])\s+(.+)$/.exec(line);
      if (fence || heading || bullet || !line.trim()) flush();
      if (!bullet) list = null;
      if (fence) pendingCode = { mark: fence[1], language: fence[2].trim().slice(0, 30), lines: [] };
      else if (heading) {
        const block = node(`h${Math.min(heading[1].length + 2, 5)}`);
        inline(block, heading[2].replace(/\s+#+\s*$/, ''));
        output.append(block);
      } else if (bullet) {
        const kind = /^\d/.test(bullet[1]) ? 'ol' : 'ul';
        if (list?.localName !== kind) {
          list = node(kind);
          if (kind === 'ol') list.start = Number.parseInt(bullet[1], 10);
          output.append(list);
        }
        const item = node('li');
        inline(item, bullet[2]);
        list.append(item);
      } else if (line.trim()) paragraph.push(line.trim());
    }
    if (pendingCode) finishCode();
    flush();
    return output;
  }

  class ChatRenderer {
    constructor(container, copyText, openAttachment) {
      this.container = container;
      this.copyText = copyText;
      this.openAttachment = openAttachment;
      this.cards = new Map();
    }

    render(messages) {
      const expected = new Set(messages.map(message => message.id));
      for (const [id, card] of this.cards) {
        if (expected.has(id)) continue;
        card.article.remove();
        this.cards.delete(id);
      }

      let position = this.container.firstElementChild;
      for (const message of messages) {
        let card = this.cards.get(message.id);
        if (!card) {
          card = this.createCard(message);
          this.cards.set(message.id, card);
        }
        if (card.article !== position) this.container.insertBefore(card.article, position);
        position = card.article.nextElementSibling;
        if (message.text === card.text) continue;
        card.text = message.text;
        if (message.agent) card.body.replaceChildren(format(message.text));
        else card.body.textContent = message.text;
      }
    }

    createCard(message) {
      const article = node('article');
      article.className = message.agent ? 'message agent' : 'message user';
      const header = node('header');
      header.append(node('span', message.agent ? 'Antigravity' : 'Você'));
      const time = node('time', new Date(message.at).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' }));
      time.dateTime = message.at;
      header.append(time);
      const body = node('div');
      body.className = 'message-body';
      const card = { article, body, text: null };
      if (message.agent) {
        const copy = node('button', 'Copiar');
        copy.type = 'button';
        copy.className = 'copy-answer';
        copy.setAttribute('aria-label', 'Copiar resposta');
        copy.addEventListener('click', () => this.copyText(card.text));
        header.append(copy);
      }
      article.append(header, body);
      if (message.attachments?.length) {
        const files = node('div');
        files.className = 'attachment-list sent-attachments';
        for (const attachment of message.attachments) {
          const link = node('button', attachment.name);
          link.className = 'file-chip';
          link.type = 'button';
          link.title = attachment.truncated ? 'Texto enviado parcialmente. Clique para baixar o original.' : attachment.name;
          link.addEventListener('click', () => this.openAttachment(attachment));
          files.append(link);
        }
        article.append(files);
      }
      return card;
    }
  }

  window.OrbitaChatRenderer = ChatRenderer;
})();
