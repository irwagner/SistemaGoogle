"""Produção gráfica e sonora original do piloto O Último Sinal."""

import argparse
import math
import os
import random
import struct
import subprocess
import wave
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


WIDTH, HEIGHT, FPS = 640, 360, 24
AMBER = (254, 209, 129)
INK = (10, 27, 39)


def font(name, size):
    return ImageFont.truetype(str(Path(os.environ["WINDIR"]) / "Fonts" / name), size)


def backdrop():
    rng = random.Random(9214)
    canvas = Image.new("RGB", (WIDTH, HEIGHT))
    pixels = canvas.load()
    for y in range(HEIGHT):
        for x in range(WIDTH):
            sunset = max(0, 1 - abs(y - 225) / 160) * max(0, 1 - abs(x - 420) / 440)
            noise = rng.choice((-4, -2, -1, 0, 1, 2, 4))
            pixels[x, y] = (
                int(11 + 41 * sunset + noise),
                int(28 + 36 * sunset + noise),
                int(45 + 22 * sunset + noise),
            )
    return canvas


def frame(background, shot, time, broken=False, cover=False):
    canvas = background.copy()
    draw = ImageDraw.Draw(canvas)
    for x, y in ((46, 37), (112, 62), (149, 34), (278, 77), (310, 34),
                 (421, 42), (508, 79), (555, 30), (589, 58), (603, 90)):
        draw.point((x, y), fill=(176, 176, 157))
    draw.ellipse((515, 48, 537, 70), fill=(209, 202, 143))
    draw.ellipse((522, 44, 542, 64), fill=(21, 36, 51))
    for opening, color in ((198, (41, 63, 74)), (209, (30, 56, 66)), (225, (20, 51, 63))):
        draw.polygon([(0, opening), (60, opening - 7), (118, opening - 5), (167, opening - 12),
                      (285, opening + 3), (374, opening - 14), (493, opening - 8),
                      (640, opening + 2), (640, 360), (0, 360)], fill=color)
    for y in range(228, 353, 5):
        for cell in range(0, 660, 41):
            shift = math.sin(time * 1.5 + y * .07 + cell * .16)
            left = cell + shift * 8 - 10
            color = (38, int(74 + 12 * shift), int(82 + 11 * shift))
            draw.line((left, y, left + 13 + 7 * shift, y), fill=color, width=1)
    if shot == 1:
        lx, ly, scale, bx, by = 154, 122, 1.0, 382 + time * 15, 259
    elif shot == 2:
        lx, ly, scale, bx, by = 257 - time * 2, 77 + time * 2, 1.55, 505 + time * 5, 275
    else:
        lx, ly, scale, bx, by = 103, 148, .62, 201 + time * 39, 261
    by += math.sin(time * 2.1) * 2

    for ray in range(10):
        target = 51 + 80 * math.sin(time * .65)
        size = 15 + ray * 4
        beam = Image.new("RGBA", canvas.size)
        ImageDraw.Draw(beam).polygon([(lx, ly), (WIDTH, target - size), (WIDTH, target + size)],
                                    fill=(245, 111, 80, 5) if broken else (252, 212, 130, 6))
        canvas.paste(beam, (0, 0), beam)

    draw = ImageDraw.Draw(canvas)
    sx = lambda x: lx + scale * x
    sy = lambda y: ly + scale * y
    draw.polygon([(0, sy(84)), (sx(-53), sy(75)), (sx(-13), sy(69)), (sx(41), sy(80)),
                  (sx(59), sy(104)), (0, sy(126))], fill=INK)
    draw.polygon([(sx(-12), sy(71)), (sx(-8), sy(8)), (sx(8), sy(8)), (sx(13), sy(71))],
                 fill=(161, 164, 147), outline=(9, 31, 43), width=2)
    for stripe in (23, 45):
        draw.polygon([(sx(-10), sy(stripe)), (sx(10), sy(stripe)), (sx(11), sy(stripe + 8)), (sx(-11), sy(stripe + 8))],
                     fill=(64, 100, 103))
    draw.rectangle((sx(-10), sy(-6), sx(10), sy(8)), fill=(229, 93, 69) if broken else AMBER, outline=INK)
    draw.polygon([(sx(-16), sy(-5)), (sx(0), sy(-18)), (sx(16), sy(-5))], fill=INK)
    draw.line((sx(0), sy(-6), sx(0), sy(8)), fill=INK, width=2)

    draw.polygon([(bx - 35, by), (bx + 26, by), (bx + 12, by + 10), (bx - 24, by + 10)], fill=(8, 27, 36))
    draw.rectangle((bx - 12, by - 12, bx + 10, by - 1), fill=(143, 168, 149))
    draw.line((bx, by - 13, bx, by - 28), fill=(150, 169, 150), width=2)
    draw.ellipse((bx - 2, by - 30, bx + 2, by - 26), fill=AMBER)
    draw.line((bx - 47, by + 6, bx - 75, by + 9), fill=(105, 138, 128))
    if shot == 3:
        draw.polygon([(370, 250), (380, 236), (389, 250), (382, 260), (376, 260)], fill=(189, 110, 68))
        draw.ellipse((378, 233, 383, 239), fill=AMBER)
    if cover or shot == 1 and time < 3.6:
        draw.text((33, 292 if cover else 282), "O Último Sinal", fill=(240, 229, 194), font=font("georgia.ttf", 32))
        draw.text((35, 332 if cover else 322), "UM CURTA DE ANIMAÇÃO", fill=(194, 208, 173), font=font("segoeui.ttf", 13))
    return canvas


def video(path, background, shot, broken=False):
    ffmpeg = os.environ.get("CENTRAL_FFMPEG", "ffmpeg")
    command = [ffmpeg, "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{WIDTH}x{HEIGHT}",
               "-r", str(FPS), "-i", "-", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "fast",
               "-threads", "2", "-movflags", "+faststart", str(path)]
    with subprocess.Popen(command, stdin=subprocess.PIPE) as process:
        for index in range(8 * FPS):
            process.stdin.write(frame(background, shot, index / FPS, broken).tobytes())
        process.stdin.close()
        if process.wait():
            raise RuntimeError(f"Falha ao codificar {path.name}.")


def audio(path, music):
    rate = 24000
    rng = random.Random(842)
    samples = bytearray()
    wash = 0
    for index in range(rate * 24):
        time = index / rate
        envelope = min(1, time / 1.4, (24 - time) / 1.5)
        if music:
            note = (146.83, 174.61, 220, 196, 130.81, 164.81)[min(5, int(time / 4))]
            local = time % 4
            pluck = (math.sin(2 * math.pi * note * local) + .30 * math.sin(4 * math.pi * note * local))
            motif = .17 * pluck * min(1, local * 20) * math.exp(-local / 2)
            sample = motif + .036 * math.sin(2 * math.pi * 73.415 * time)
        else:
            wash = .96 * wash + .04 * rng.uniform(-1, 1)
            bell_time = time - 19.8
            bell = 0 if bell_time < 0 else .22 * math.exp(-bell_time * 1.6) * (
                math.sin(2 * math.pi * 523.25 * bell_time) + .26 * math.sin(2 * math.pi * 1094 * bell_time))
            sample = wash * (.22 + .10 * math.sin(time * 1.4)) + bell
        samples.extend(struct.pack("<h", int(32767 * sample * envelope)))
    with wave.open(str(path), "wb") as file:
        file.setparams((1, 2, rate, 0, "NONE", "not compressed"))
        file.writeframes(samples)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("destino", type=Path)
    target = parser.parse_args().destino
    target.mkdir(parents=True, exist_ok=True)
    background = backdrop()
    for name, shot, broken in (("plano-1", 1, False), ("plano-2-vermelho", 2, True),
                               ("plano-2-ambar", 2, False), ("plano-3", 3, False)):
        video(target / f"{name}.mp4", background, shot, broken)
    frame(background, 1, 2.1, cover=True).save(target / "capa.png")
    audio(target / "trilha.wav", music=True)
    audio(target / "efeitos.wav", music=False)


if __name__ == "__main__":
    main()
