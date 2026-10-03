using System.Text;
using Assistant.Application.Health;

namespace Assistant.UnitTests.Application.Health;

public class DoseAdviceFilterTests
{
    private const string ExpectedRefusal =
        "Я не даю советов по дозам лекарств. Это вопрос к врачу — запишите его, чтобы спросить на приёме.";

    [Theory]
    [InlineData("Увеличьте дозу инсулина на 2 единицы.")]
    [InlineData("Можно уменьшить дозу на ночь.")]
    [InlineData("Попробуйте снизить на 2 ед перед ужином.")]
    [InlineData("Повысьте короткий на 1 ЕД.")]
    [InlineData("Добавьте 2 единицы к утренней дозе.")]
    [InlineData("Убавьте 4 ед.")]
    [InlineData("Стоит изменить дозировку.")]
    [InlineData("Я бы поднял дозу.")]
    [InlineData("Введите 6 единиц перед едой.")]
    [InlineData("Вводите по 4 ед.")]
    [InlineData("Вколите 8 ед. короткого.")]
    [InlineData("Примите 500 мг.")]
    [InlineData("Принимайте по 2 таблетки.")]
    [InlineData("Пропустите вечернюю дозу.")]
    [InlineData("Отмените таблетки на сегодня.")]
    [InlineData("Замените препарат на другой.")]
    [InlineData("Сократите дозу вдвое.")]
    [InlineData("Удвойте дозу.")]
    [InlineData("Дозу лучше скорректировать.")]
    [InlineData("Дозировку стоит пересмотреть.")]
    [InlineData("Колите на 2 единицы больше.")]
    [InlineData("Нужно на 2 единицы меньше.")]
    [InlineData("Можно добавить 10 МЕ.")]
    [InlineData("Было 6 ед. Увеличьте до 8.")]
    [InlineData("Сахар высокий.\nДобавьте 2 ед короткого.")]
    [InlineData("Increase the dose by 2 units.")]
    [InlineData("You could reduce your insulin by 4 units.")]
    [InlineData("Take 10 units before dinner.")]
    [InlineData("Skip the evening dose.")]
    [InlineData("Inject 4 IU now.")]
    [InlineData("Add 2u to the morning shot.")]
    public void Dose_advice_is_detected(string answer)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeTrue();
    }

    // Word forms, synonyms and phrasings beyond the basic table: infinitives, prefixed verbs,
    // advice without a verb ("нужно 8 ед"), per-day units, upper case, a dose split from its
    // change by a blank line.
    [Theory]
    [InlineData("Поменяйте дозировку.")]
    [InlineData("Подкорректируйте дозу на ужин.")]
    [InlineData("Принять 2 таблетки утром.")]
    [InlineData("Сделайте укол короткого.")]
    [InlineData("Попробуйте 4 единицы.")]
    [InlineData("Вам нужно 8 ед на ужин.")]
    [InlineData("Ставьте 6 ед. перед завтраком.")]
    [InlineData("Можно побольше единиц на ужин.")]
    [InlineData("Сбавьте инсулин.")]
    [InlineData("Понизьте вечернюю дозу.")]
    [InlineData("Прекратите приём таблеток.")]
    [InlineData("Не пейте таблетки на ночь.")]
    [InlineData("Продолжайте пить витамины.")]
    [InlineData("Разделите дозу на два укола.")]
    [InlineData("Перенесите вечерний укол на утро.")]
    [InlineData("Примите 500 мг/сут.")]
    [InlineData("УВЕЛИЧЬТЕ ДОЗУ.")]
    [InlineData("Введите 6 ед.")]
    [InlineData("Было 6 ед.\n\nУвеличьте до 8.")]
    [InlineData("Было 6 ед. перед ужином. Увеличьте до 8.")]
    [InlineData("Take 500 mg/day.")]
    [InlineData("Bump your basal up by 2.")]
    [InlineData("Give yourself 4 units.")]
    [InlineData("You need 8 units at dinner.")]
    [InlineData("Stop the tablets.")]
    [InlineData("Halve the dose.")]
    public void Other_word_forms_of_dose_advice_are_detected(string answer)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeTrue();
    }

    [Fact]
    public void A_heading_line_carries_the_dose_word_to_every_item_under_it()
    {
        const string answer =
            "Инсулин на ужин:\n" +
            "- первое: проверьте сахар.\n" +
            "- второе: запишите результат.\n" +
            "- третье: добавьте 2.";

        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeTrue();
    }

    [Fact]
    public void A_blank_line_ends_the_heading()
    {
        const string answer =
            "Инсулин в записях:\n" +
            "- 08:00, 6 ед.\n" +
            "\n" +
            "Сахар записан.\n" +
            "Глюкоза повысилась до 9.1 ммоль/л после обеда.";

        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeFalse();
    }

    [Fact]
    public void Invisible_characters_inside_a_word_do_not_hide_it()
    {
        DoseAdviceFilter.ContainsDoseAdvice("Увеличьте до­зу на 2.").ShouldBeTrue();
        DoseAdviceFilter.ContainsDoseAdvice("Увелич​ьте ин‍сулин.").ShouldBeTrue();
    }

    // Format characters beyond the zero-width ones (bidi embeddings and isolates, the Mongolian vowel
    // separator) and combining stress marks inside a word.
    [Theory]
    [InlineData("Увеличьте до\u202Eзу на 2.")]
    [InlineData("Увеличьте до\u2066зу на 2.")]
    [InlineData("Увеличьте ин\u180Eсулин.")]
    [InlineData("Увеличьте до\u0301зу на 2.")]
    public void Other_invisible_characters_and_marks_do_not_hide_a_word(string answer)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeTrue();
    }

    // Answers the patterns cannot read are refused rather than passed.
    [Theory]
    [InlineData("السكر طبيعي اليوم.")]
    [InlineData("今天血糖正常。")]
    [InlineData("Η γλυκόζη είναι φυσιολογική σήμερα.")]
    [InlineData("Сахар в норме. 今天血糖正常。")]
    [InlineData("Glucose is fine, ζ.")]
    public void Letters_outside_latin_and_cyrillic_count_as_dose_advice(string answer)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Сахар в норме 👍")]
    [InlineData("Glucose is fine today 🙂❤️")]
    [InlineData("Давление 120/80, температура 36,6 °C, шаги 8–10 тыс. — всё в порядке.")]
    [InlineData("См. пункт № 3 в записях.")]
    [InlineData("A café au lait is a drink, not a naïve choice.")]
    [InlineData("«Хороший» день: сахар 5.4 ммоль/л…")]
    public void Russian_english_symbols_and_emoji_pass_the_script_check(string answer)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeFalse();
    }

    // Latin letters inside a Cyrillic word (and Cyrillic inside a Latin word), full-width and
    // superscript forms.
    [Theory]
    [InlineData("Увеличьте дoзу на 2.")]
    [InlineData("Введите 6 eд.")]
    [InlineData("ВВЕДИТЕ 6 EД.")]
    [InlineData("Increase the dоse by 2.")]
    [InlineData("Введите \uFF16 ед.")]
    [InlineData("Ｔａｋｅ \uFF16 ｕｎｉｔｓ.")]
    [InlineData("Обычно \u2076 ед перед ужином.")]
    public void Lookalike_letters_and_wide_forms_do_not_hide_dose_advice(string answer)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Сахар в нoрме.", false)]
    [InlineData("Keep a food diary and walk after meals.", false)]
    [InlineData("Take your meds as your doctor said.", true)]
    public void Lookalike_mapping_leaves_other_text_alone(string answer, bool expected)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBe(expected);
    }

    // A number with a dose unit and a time of day is a schedule even without a change word.
    [Theory]
    [InlineData("Обычно 6 ед перед ужином.")]
    [InlineData("6 единиц утром и 4 вечером.")]
    [InlineData("2 таблетки на ночь.")]
    [InlineData("Usually 6 units at bedtime.")]
    [InlineData("4 IU before breakfast.")]
    public void A_bare_schedule_counts_as_dose_advice(string answer)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeTrue();
    }

    [Fact]
    public void Ill_formed_text_counts_as_dose_advice()
    {
        DoseAdviceFilter.ContainsDoseAdvice("Сахар в норме " + (char)0xD800).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Вы записали инсулин в 08:00. Сахар после этого снизился до 5.1.")]
    [InlineData("Обсудите изменение дозы с врачом.")]
    [InlineData("Вы записали инсулин 6 ед. в 08:00. Сахар стоит проверить через час.")]
    [InlineData("Вы записали инсулин 6 ед. перед ужином.")]
    public void Known_over_blocks_are_accepted(string answer)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Сахар 7.8 ммоль/л через час после еды выше цели 7.0. Расскажите об этом врачу.")]
    [InlineData("Если сахар ниже 3.9 ммоль/л, действуйте по плану врача.")]
    [InlineData("Вы записали инсулин 6 ед. в 08:00.")]
    [InlineData("Глюкоза повысилась до 9.1 ммоль/л после обеда.")]
    [InlineData("Сахар повысился до 180 мг/дл.")]
    [InlineData("Снижение сахара после нагрузки — частое явление.")]
    [InlineData("Добавьте в рацион больше овощей.")]
    [InlineData("Пейте больше воды, особенно утром.")]
    [InlineData("Уменьшите порции быстрых углеводов.")]
    [InlineData("Вопрос о дозировке задайте врачу.")]
    [InlineData("Не меняйте ничего сами — это вопрос к врачу.")]
    [InlineData("За неделю вес увеличился на 1 кг.")]
    [InlineData("Walk after dinner.")]
    [InlineData("Сахар 180 мг/дл после обеда.")]
    [InlineData("Прогулка 30 минут после ужина.")]
    [InlineData("Давление 150/95 выше порога 140 — свяжитесь с врачом.")]
    [InlineData("Increase your water intake.")]
    [InlineData("Your glucose decreased to 5.2 mmol/L.")]
    [InlineData("Doses are a question for your doctor.")]
    [InlineData("Не заменяю врача.")]
    [InlineData("")]
    [InlineData("Вы записали инсулин 6 ед. в 08:00, сахар через час 7.8 ммоль/л.")]
    [InlineData("Glucose went up to 180 mg/dL after lunch.")]
    [InlineData("Короткая прогулка после еды снижает сахар.")]
    [InlineData("Средний сахар за сутки 6.2 ммоль/л, это выше вчерашнего.")]
    [InlineData("Пейте больше воды — 1.5 л в день.")]
    public void Ordinary_answers_pass(string answer)
    {
        DoseAdviceFilter.ContainsDoseAdvice(answer).ShouldBeFalse();
    }

    [Fact]
    public void The_fixed_refusal_never_triggers_the_filter()
    {
        DoseAdviceFilter.RefusalText.ShouldBe(ExpectedRefusal);
        DoseAdviceFilter.ContainsDoseAdvice(DoseAdviceFilter.RefusalText).ShouldBeFalse();
        DoseAdviceFilter.Apply(DoseAdviceFilter.RefusalText).ShouldBe(ExpectedRefusal);
    }

    [Fact]
    public void Apply_replaces_the_whole_answer()
    {
        DoseAdviceFilter.Apply("Сахар в норме. Увеличьте дозу на 2 единицы. Хорошего дня!").ShouldBe(ExpectedRefusal);
        DoseAdviceFilter.Apply("Сахар в норме.").ShouldBe("Сахар в норме.");
    }

    [Fact]
    public void A_very_long_answer_completes()
    {
        var answer = new StringBuilder();
        while (answer.Length < 100_000)
        {
            answer.Append("Сахар в норме. ");
        }

        DoseAdviceFilter.ContainsDoseAdvice(answer.ToString()).ShouldBeFalse();
    }

    [Fact]
    public void A_regex_timeout_counts_as_dose_advice()
    {
        var answer = new StringBuilder();
        while (answer.Length < 2_000_000)
        {
            answer.Append("сахар в норме ");
        }

        var text = answer.ToString();

        DoseAdviceFilter.ContainsDoseAdvice(text).ShouldBeFalse();
        DoseAdviceFilter.ContainsDoseAdvice(text, TimeSpan.FromTicks(1)).ShouldBeTrue();
    }
}
