async page => {
    const origin = page.url().split("/").slice(0, 3).join("/") + "/";
    await page.route(/^https?:\/\//, route => route.request().url().startsWith(origin) ? route.continue() : route.abort());
    const input = page.locator('input[type="text"]').first();
    const text = "Verificar entrega <b>como texto</b>";
    await input.fill(text);
    await page.getByRole("button", { name: "Adicionar", exact: true }).click();
    await page.getByText(text, { exact: true }).waitFor();
    const done = page.getByRole("checkbox").first();
    await done.check();
    await page.reload();
    await page.getByText(text, { exact: true }).waitFor();
    if (!await page.getByRole("checkbox").first().isChecked())
        throw new Error("A marcação não persistiu no navegador.");
    await page.evaluate(() => { window.confirm = () => true; });
    await page.getByRole("button", { name: /Excluir|Remover/ }).first().click();
    await page.getByText(text, { exact: true }).waitFor({ state: "hidden" });
    await page.reload();
    if (await page.getByText(text, { exact: true }).count() !== 0)
        throw new Error("A exclusão não persistiu.");
    return "OK navegador · cadastro, texto literal, conclusão, recarga e exclusão do sistema produzido";
}
