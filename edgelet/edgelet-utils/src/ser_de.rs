// Copyright (c) Microsoft. All rights reserved.

use std::fmt;
use std::marker::PhantomData;
use std::str::FromStr;

use serde::de::{self, Deserialize, Deserializer, MapAccess, Visitor};
use serde_json;

// This implementation has been adapted from: https://serde.rs/string-or-struct.html

pub fn string_or_struct<'de, T, D>(deserializer: D) -> Result<T, D::Error>
where
    T: Deserialize<'de> + FromStr<Err = serde_json::Error>,
    D: Deserializer<'de>,
{
    // This is a Visitor that forwards string types to T's `FromStr` impl and
    // forwards map types to T's `Deserialize` impl. The `PhantomData` is to
    // keep the compiler from complaining about T being an unused generic type
    // parameter. We need T in order to know the Value type for the Visitor
    // impl.
    struct StringOrStruct<T>(PhantomData<fn() -> T>);

    impl<'de, T> Visitor<'de> for StringOrStruct<T>
    where
        T: Deserialize<'de> + FromStr<Err = serde_json::Error>,
    {
        type Value = T;

        fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
            formatter.write_str("string or map")
        }

        fn visit_str<E>(self, value: &str) -> Result<T, E>
        where
            E: de::Error,
        {
            FromStr::from_str(value).map_err(de::Error::custom)
        }

        fn visit_map<M>(self, visitor: M) -> Result<T, M::Error>
        where
            M: MapAccess<'de>,
        {
            // `MapAccessDeserializer` is a wrapper that turns a `MapAccess`
            // into a `Deserializer`, allowing it to be used as the input to T's
            // `Deserialize` implementation. T then deserializes itself using
            // the entries from the map visitor.
            Deserialize::deserialize(de::value::MapAccessDeserializer::new(visitor))
        }
    }

    deserializer.deserialize_any(StringOrStruct(PhantomData))
}

#[cfg(test)]
mod tests {
    use std::str::FromStr;

    use serde_json;

    use ser_de::string_or_struct;

    #[derive(Debug, Deserialize)]
    struct Options {
        opt1: String,
        opt2: Option<String>,
    }

    impl FromStr for Options {
        type Err = serde_json::Error;

        fn from_str(s: &str) -> Result<Self, Self::Err> {
            serde_json::from_str(s)
        }
    }

    #[derive(Debug, Deserialize)]
    struct Container {
        #[serde(deserialize_with = "string_or_struct")]
        options: Options,
    }

    #[test]
    fn deser_from_map() {
        let container_json = json!({
            "options": {
                "opt1": "val1",
                "opt2": "val2"
            }
		}).to_string();

        let container: Container = serde_json::from_str(&container_json).unwrap();
        assert_eq!(&container.options.opt1, "val1");
        assert_eq!(&container.options.opt2.unwrap(), "val2");
    }

    #[test]
    fn deser_from_str() {
        let container_json = json!({
            "options": json!({
                "opt1": "val1",
                "opt2": "val2"
            }).to_string()
		}).to_string();

        let container: Container = serde_json::from_str(&container_json).unwrap();
        assert_eq!(&container.options.opt1, "val1");
        assert_eq!(&container.options.opt2.unwrap(), "val2");
    }

    #[test]
    #[should_panic]
    fn deser_from_bad_str_fails() {
        let container_json = json!({
            "options": "not really json you know"
		}).to_string();

        let _container: Container = serde_json::from_str(&container_json).unwrap();
    }
}