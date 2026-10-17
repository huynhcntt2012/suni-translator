def srt_time(seconds):
    milliseconds = max(0, round(seconds * 1000))
    hours, milliseconds = divmod(milliseconds, 3600000)
    minutes, milliseconds = divmod(milliseconds, 60000)
    seconds, milliseconds = divmod(milliseconds, 1000)
    return f"{hours:02}:{minutes:02}:{seconds:02},{milliseconds:03}"

def to_srt(rows, bilingual=True):
    result = []
    for i, (start, end, language, original, translated, latency) in enumerate(rows, 1):
        lines = f"{original}\n{translated}" if bilingual else translated
        result.append(f"{i}\n{srt_time(start)} --> {srt_time(max(start + 0.1, end))}\n{lines}\n")
    return "\n".join(result)
