async page => {
    await page.getByRole("heading", { name: "O que você quer produzir?" }).waitFor();
    await page.getByRole("link", { name: "Criar sistema" }).click();
    await page.getByLabel("Nome do novo projeto").fill("Ensaio de navegação");
    await page.getByLabel("Título do trabalho").fill("Verificar rascunho");
    await page.getByLabel("O que você quer produzir?").fill("Quero um quadro de tarefas.");
    await page.getByRole("button", { name: "Abrir trabalho" }).click();
    try {
        await page.getByRole("heading", { name: "Verificar rascunho", exact: true }).waitFor({ timeout: 7000 });
    } catch {
        const invalid = await page.locator(":invalid").evaluateAll(items => items.map(item => [item.name, item.validationMessage]));
        throw new Error("O formulário não abriu o trabalho: " + page.url() + " · " + (await page.locator("body").innerText()).slice(-900) + " · " + JSON.stringify(invalid));
    }
    const request = page.getByLabel("Descrição do resultado");
    const text = "Quero um quadro de tarefas persistente, com categorias.";
    await request.fill(text);
    await page.getByText("Rascunho salvo", { exact: true }).waitFor();
    await page.reload();
    if (await request.inputValue() !== text) throw new Error("O rascunho sumiu ao recarregar.");
    await page.getByRole("link", { name: "Vídeo", exact: true }).click();
    await page.getByRole("heading", { name: "Vídeo e minisséries", exact: true }).waitFor();
    if (await page.getByText(text).count() !== 0) throw new Error("A conversa apareceu na outra área.");
    await page.getByRole("link", { name: "Central de Produção" }).click();
    await page.getByRole("link", { name: "Verificar rascunho" }).click();
    if (await request.inputValue() !== text) throw new Error("O rascunho sumiu ao alternar áreas.");
    return "OK navegador · entrada Criar sistema, gravação automática, recarga, troca de área e retomada";
}
